using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Umbraco.Extensions;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.PublishedCache;
using AutoBlockList.Services.interfaces;
using Umbraco.Cms.Infrastructure.Scoping;
using static Umbraco.Cms.Core.Constants;

namespace AutoBlockList.Services
{
	public class FinalizeReport
	{
		public bool DryRun { get; set; }
		public List<string> PropertyRenames { get; set; } = new();
		public List<string> Skipped { get; set; } = new();
		public int ContentRowsUpdated { get; set; }
		public List<string> DataTypesDeleted { get; set; } = new();
		public List<string> DataTypesKept { get; set; } = new();
		public List<string> DataTypesRenamed { get; set; } = new();
		public List<string> Errors { get; set; } = new();
	}

	public interface INestedContentFinalizeService
	{
		FinalizeReport Finalize(bool dryRun);
	}

	/// <summary>
	/// Final step after converting Nested Content to Block List. For every content type
	/// (document types, element types and compositions) with a Nested Content property "x"
	/// and its converted Block List sibling "xBL" (per AliasFormatting):
	///   1. Renames the "xBL" keys inside all stored block JSON (every version) to "x", so
	///      nested block data keeps matching the element type properties after step 2.
	///   2. Removes the Nested Content property "x" and renames "xBL" to "x". The property
	///      type keeps its id, so its data is kept.
	///   3. Deletes Nested Content data types that are no longer used.
	///   4. Renames "[Block list] - [NC] Foo" data types to "Foo".
	/// Everything runs in one transaction. Then the published cache is rebuilt.
	/// </summary>
	public class NestedContentFinalizeService : INestedContentFinalizeService
	{
		private const string NcPrefix = "[NC] ";

		private readonly ILogger<NestedContentFinalizeService> _logger;
		private readonly IScopeProvider _scopeProvider;
		private readonly IContentTypeService _contentTypeService;
		private readonly IDataTypeService _dataTypeService;
		private readonly IAutoBlockListService _autoBlockListService;
		private readonly IPublishedSnapshotService _publishedSnapshotService;
		private readonly DistributedCache _distributedCache;

		public NestedContentFinalizeService(
			ILogger<NestedContentFinalizeService> logger,
			IScopeProvider scopeProvider,
			IContentTypeService contentTypeService,
			IDataTypeService dataTypeService,
			IAutoBlockListService autoBlockListService,
			IPublishedSnapshotService publishedSnapshotService,
			DistributedCache distributedCache)
		{
			_logger = logger;
			_scopeProvider = scopeProvider;
			_contentTypeService = contentTypeService;
			_dataTypeService = dataTypeService;
			_autoBlockListService = autoBlockListService;
			_publishedSnapshotService = publishedSnapshotService;
			_distributedCache = distributedCache;
		}

		private record PlannedRename(int ContentTypeId, string ContentTypeAlias, string NcAlias, string BlAlias, int BlPropertyTypeId);

		private class PropertyDataRow
		{
			public int Id { get; set; }
			public string? TextValue { get; set; }
		}

		public FinalizeReport Finalize(bool dryRun)
		{
			var report = new FinalizeReport { DryRun = dryRun };
			var aliasFormatting = _autoBlockListService.GetAliasFormatting();

			using var scope = _scopeProvider.CreateScope();

			try
			{
				var allTypes = _contentTypeService.GetAll().ToList();

				// 1. Plan: NC property + its Block List sibling on the same content type.
				var plan = new List<PlannedRename>();
				foreach (var type in allTypes)
				{
					foreach (var nc in type.PropertyTypes.Where(p => p.PropertyEditorAlias == PropertyEditors.Aliases.NestedContent))
					{
						var blAlias = string.Format(aliasFormatting, nc.Alias);
						var bl = type.PropertyTypes.FirstOrDefault(p => p.Alias == blAlias);

						if (bl == null || !IsBlockEditor(bl.PropertyEditorAlias))
						{
							report.Skipped.Add($"{type.Alias}.{nc.Alias}: no Block List property '{blAlias}' found - convert it first");
							continue;
						}

						plan.Add(new PlannedRename(type.Id, type.Alias, nc.Alias, blAlias, bl.Id));
						report.PropertyRenames.Add($"{type.Alias}: remove '{nc.Alias}' (Nested Content), rename '{blAlias}' -> '{nc.Alias}'");
					}
				}

				// Per element type key: the block JSON keys to rename. Includes types that get the
				// renamed property through a composition.
				var blIds = plan.ToDictionary(p => p.BlPropertyTypeId, p => p);
				var keyRenamesByType = new Dictionary<Guid, Dictionary<string, string>>();
				foreach (var type in allTypes)
				{
					var map = type.CompositionPropertyTypes
						.Where(p => blIds.ContainsKey(p.Id))
						.Select(p => blIds[p.Id])
						.DistinctBy(p => p.BlAlias)
						.ToDictionary(p => p.BlAlias, p => p.NcAlias);
					if (map.Count > 0)
						keyRenamesByType[type.Key] = map;
				}

				// 2. Rename keys inside stored Block List / Block Grid JSON (all versions).
				report.ContentRowsUpdated = RewriteBlockJson(scope, keyRenamesByType, dryRun);

				// 3. Remove NC property, rename BL property.
				if (!dryRun)
				{
					foreach (var group in plan.GroupBy(p => p.ContentTypeId))
					{
						var type = _contentTypeService.Get(group.Key);
						if (type == null)
							continue;

						foreach (var rename in group)
						{
							type.RemovePropertyType(rename.NcAlias);
							var bl = type.PropertyTypes.First(p => p.Id == rename.BlPropertyTypeId);
							bl.Alias = rename.NcAlias;
						}

						_contentTypeService.Save(type);
					}
				}

				// 4. Delete NC data types that nothing uses any more (after step 3).
				var renamedNcNames = new HashSet<string>();
				foreach (var ncDataType in _dataTypeService.GetByEditorAlias(PropertyEditors.Aliases.NestedContent).ToList())
				{
					var stillUsed = dryRun
						? StillUsedAfterPlan(ncDataType, allTypes, plan)
						: _dataTypeService.GetReferences(ncDataType.Id).Any();

					if (stillUsed)
					{
						report.DataTypesKept.Add($"{ncDataType.Name}: still used by a property that has no Block List sibling");
						continue;
					}

					if (!dryRun)
						_dataTypeService.Delete(ncDataType);

					report.DataTypesDeleted.Add(ncDataType.Name ?? ncDataType.Id.ToString());
				}

				// 5. Rename "[Block list] - X" data types to X (without the "[NC] " prefix).
				var nameFormatting = _autoBlockListService.GetNameFormatting();
				var (prefix, suffix) = SplitFormat(nameFormatting);
				var existingNames = _dataTypeService.GetAll()
					.Where(d => d.EditorAlias != PropertyEditors.Aliases.NestedContent || !report.DataTypesDeleted.Contains(d.Name ?? ""))
					.Select(d => d.Name ?? "")
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				foreach (var blDataType in _dataTypeService.GetAll().Where(d => IsBlockEditor(d.EditorAlias)).ToList())
				{
					var name = blDataType.Name ?? "";
					if (prefix.Length + suffix.Length == 0 || !name.StartsWith(prefix) || !name.EndsWith(suffix) || name.Length <= prefix.Length + suffix.Length)
						continue;

					var newName = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
					if (newName.StartsWith(NcPrefix))
						newName = newName.Substring(NcPrefix.Length);

					if (existingNames.Contains(newName))
					{
						report.Skipped.Add($"Data type '{name}': not renamed, '{newName}' already exists");
						continue;
					}

					if (!dryRun)
					{
						blDataType.Name = newName;
						_dataTypeService.Save(blDataType);
					}

					existingNames.Add(newName);
					report.DataTypesRenamed.Add($"'{name}' -> '{newName}'");
				}

				if (!dryRun)
					scope.Complete();
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Failed to finalize nested content conversion");
				report.Errors.Add($"{ex.GetType().Name}: {ex.Message} - nothing was changed (rolled back)");
				return report;
			}

			if (!dryRun)
			{
				// Content JSON was changed directly in the database: rebuild the published cache.
				_publishedSnapshotService.Rebuild();
				_distributedCache.RefreshAllPublishedSnapshot();
			}

			return report;
		}

		private static bool IsBlockEditor(string editorAlias) =>
			editorAlias == PropertyEditors.Aliases.BlockList || editorAlias == PropertyEditors.Aliases.BlockGrid;

		private static (string prefix, string suffix) SplitFormat(string format)
		{
			var i = format.IndexOf("{0}", StringComparison.Ordinal);
			return i < 0 ? ("", "") : (format.Substring(0, i), format.Substring(i + 3));
		}

		private static bool StillUsedAfterPlan(IDataType ncDataType, List<IContentType> allTypes, List<PlannedRename> plan)
		{
			var removed = plan.Select(p => (p.ContentTypeId, p.NcAlias)).ToHashSet();
			return allTypes.Any(t => t.PropertyTypes.Any(p =>
				p.DataTypeId == ncDataType.Id && !removed.Contains((t.Id, p.Alias))));
		}

		private int RewriteBlockJson(IScope scope, Dictionary<Guid, Dictionary<string, string>> keyRenamesByType, bool dryRun)
		{
			if (keyRenamesByType.Count == 0)
				return 0;

			var database = scope.Database;
			var updated = 0;
			var lastId = 0;
			const int batchSize = 500;

			while (true)
			{
				var rows = database.Fetch<PropertyDataRow>(
					$@"SELECT TOP {batchSize} pd.id AS Id, pd.textValue AS TextValue
					   FROM umbracoPropertyData pd
					   INNER JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
					   INNER JOIN umbracoDataType dt ON dt.nodeId = pt.dataTypeId
					   WHERE dt.propertyEditorAlias IN (@0, @1) AND pd.textValue IS NOT NULL AND pd.id > @2
					   ORDER BY pd.id",
					PropertyEditors.Aliases.BlockList, PropertyEditors.Aliases.BlockGrid, lastId);

				if (rows.Count == 0)
					break;

				foreach (var row in rows)
				{
					lastId = row.Id;
					var newJson = RenameKeysInBlockValue(row.TextValue, keyRenamesByType);
					if (newJson == null)
						continue;

					updated++;
					if (!dryRun)
						database.Execute("UPDATE umbracoPropertyData SET textValue = @0 WHERE id = @1", newJson, row.Id);
				}
			}

			return updated;
		}

		/// <summary>Returns the rewritten JSON, or null when nothing changed.</summary>
		private static string? RenameKeysInBlockValue(string? json, Dictionary<Guid, Dictionary<string, string>> keyRenamesByType)
		{
			var root = Parse(json) as JObject;
			if (root == null)
				return null;

			return RenameKeys(root, keyRenamesByType) ? root.ToString(Formatting.None) : null;
		}

		private static bool RenameKeys(JObject blockValue, Dictionary<Guid, Dictionary<string, string>> keyRenamesByType)
		{
			var changed = false;

			foreach (var listName in new[] { "contentData", "settingsData" })
			{
				if (blockValue[listName] is not JArray items)
					continue;

				foreach (var item in items.OfType<JObject>())
				{
					if (Guid.TryParse(item.Value<string>("contentTypeKey"), out var typeKey)
						&& keyRenamesByType.TryGetValue(typeKey, out var renames))
					{
						foreach (var (blAlias, ncAlias) in renames)
						{
							var prop = item.Property(blAlias);
							if (prop == null || item.Property(ncAlias) != null)
								continue;

							prop.Replace(new JProperty(ncAlias, prop.Value));
							changed = true;
						}
					}

					// Nested block values: stored either as an object or as a JSON string.
					foreach (var prop in item.Properties().ToList())
					{
						if (prop.Value is JObject nestedObject && nestedObject["contentData"] != null)
						{
							changed |= RenameKeys(nestedObject, keyRenamesByType);
						}
						else if (prop.Value.Type == JTokenType.String
							&& prop.Value.Value<string>() is { } s
							&& s.TrimStart().StartsWith("{")
							&& s.Contains("contentData")
							&& Parse(s) is JObject nestedFromString
							&& RenameKeys(nestedFromString, keyRenamesByType))
						{
							prop.Value = nestedFromString.ToString(Formatting.None);
							changed = true;
						}
					}
				}
			}

			return changed;
		}

		private static JToken? Parse(string? json)
		{
			if (string.IsNullOrWhiteSpace(json))
				return null;

			try
			{
				using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
				return JToken.ReadFrom(reader);
			}
			catch (JsonException)
			{
				return null;
			}
		}
	}
}
