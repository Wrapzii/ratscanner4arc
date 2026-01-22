using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RatScanner;

/// <summary>
/// Static hardcoded data for Arc Raiders items
/// Source: https://metaforge.app/api/arc-raiders/items
/// </summary>
public static class ArcRaidersData {
	public static event Action? AuxDataUpdated;
	private static readonly object OverrideReloadLock = new();
	private static FileSystemWatcher? OverrideWatcher;
	private static DateTime LastOverrideReloadUtc = DateTime.MinValue;
	private static readonly object RecycleFetchLock = new();
	private static readonly HashSet<string> PendingRecycleOutputFetches = new(StringComparer.OrdinalIgnoreCase);
	private static CraftingWeightsConfig? CraftingWeights;
	
	/// <summary>
	/// Simplified item class for Arc Raiders with hardcoded values
	/// </summary>
	public class ArcItem {
		public string Id { get; set; } = "";
		public string Name { get; set; } = "";
		public string ShortName { get; set; } = "";
		public int Width { get; set; } = 1;
		public int Height { get; set; } = 1;
		public int Value { get; set; } = 0; // Static value for Arc Raiders (no dynamic market)
		public int RecycleValue { get; set; } = 0; // Estimated recycle value (if known)
		public List<RecycleOutput> RecycleOutputs { get; set; } = new();
		public bool IsQuestItem { get; set; } = false;
		public bool IsBaseItem { get; set; } = false; // For base building
		public bool IsRecyclable { get; set; } = false;
		public bool IsCraftingItem { get; set; } = false;
		public string? CraftingNote { get; set; }
		public int UsedInCount { get; set; } = 0;
		public bool HasCraftingUsageData { get; set; } = false;
		public string Rarity { get; set; } = "";
		public double Weight { get; set; } = 0;
		public string? ImageLink { get; set; }
		public string? WikiLink { get; set; }
		public string Category { get; set; } = "General";
		
		// Calculated property
		public int ValuePerSlot => Value / Math.Max(1, Width * Height);
	}

	public class RecycleOutput {
		public string Id { get; set; } = "";
		public string? Name { get; set; }
		public int Quantity { get; set; } = 1;
	}

	public class CraftingWeightsConfig {
		public int KeepScoreThreshold { get; set; } = 6;
		public int RecycleOutputScoreThreshold { get; set; } = 6;
		public Dictionary<string, int> ItemWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}
	
	/// <summary>
	/// Hardcoded Arc Raiders items database loaded from RaidTheory repository or fallback to legacy JSON.
	/// Primary source: https://github.com/RaidTheory/arcraiders-data
	/// Fallback: Resources/ArcRaidersItems.json
	/// </summary>
	private static readonly Lazy<List<ArcItem>> Items = new(LoadItems);
	private static readonly HttpClient MetaforgeClient = new();
	private const int MetaforgeDelayMs = 150;

	private static List<ArcItem> LoadItems() {
		try {
			// Try to load from RaidTheory data first
			if (RaidTheoryDataSource.IsDataAvailable()) {
				Logger.LogInfo("Loading items from RaidTheory data repository...");
				var items = LoadItemsFromRaidTheory();
				if (items.Count > 0) {
					Logger.LogInfo($"Successfully loaded {items.Count} items from RaidTheory data");
					ApplyRecycleValueOverrides(items);
					ApplyRecycleOutputOverrides(items);
					ApplyCraftingUsageOverrides(items);
					ApplyCraftingKeepOverrides(items);
					EnsureOverrideWatchers();
					
					// Start async data download in background if needed
					_ = Task.Run(async () => {
						try {
							await RaidTheoryDataSource.EnsureDataAsync().ConfigureAwait(false);
						} catch (Exception ex) {
							Logger.LogDebug($"Background data refresh failed: {ex.Message}");
						}
					});
					
					return items;
				}
			}
			
			// Fallback to legacy JSON file
			Logger.LogInfo("Loading items from legacy JSON file...");
			string dataPath = GetDataPath();
			if (!File.Exists(dataPath)) {
				Logger.LogWarning("No data source available, attempting to download RaidTheory data...");
				// Try to download data synchronously as last resort
				try {
					var downloadTask = RaidTheoryDataSource.DownloadDataAsync();
					// Use GetAwaiter().GetResult() to avoid potential deadlock
					bool success = downloadTask.GetAwaiter().GetResult();
					if (success && RaidTheoryDataSource.IsDataAvailable()) {
						var items = LoadItemsFromRaidTheory();
						if (items.Count > 0) {
							ApplyRecycleValueOverrides(items);
							ApplyRecycleOutputOverrides(items);
							ApplyCraftingUsageOverrides(items);
							ApplyCraftingKeepOverrides(items);
							EnsureOverrideWatchers();
							return items;
						}
					}
				} catch (Exception downloadEx) {
					Logger.LogWarning($"Failed to download RaidTheory data: {downloadEx.Message}");
				}
				return new List<ArcItem>();
			}

			string json = File.ReadAllText(dataPath);
			var legacyItems = JsonSerializer.Deserialize<List<ArcItem>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			
			if (legacyItems == null) return new List<ArcItem>();

			ApplyRecycleValueOverrides(legacyItems);
			ApplyRecycleOutputOverrides(legacyItems);
			ApplyCraftingUsageOverrides(legacyItems);
			ApplyCraftingKeepOverrides(legacyItems);
			EnsureAuxData(legacyItems);
			EnsureOverrideWatchers();
			
			// Start async data download in background for future use
			_ = Task.Run(async () => {
				try {
					await RaidTheoryDataSource.EnsureDataAsync().ConfigureAwait(false);
				} catch (Exception ex) {
					Logger.LogDebug($"Background data refresh failed: {ex.Message}");
				}
			});
			
			return legacyItems;
		} catch (Exception ex) {
			Logger.LogError($"Failed to load items: {ex.Message}");
			return new List<ArcItem>();
		}
	}
	
	private static List<ArcItem> LoadItemsFromRaidTheory() {
		var raidTheoryItems = RaidTheoryDataSource.LoadItems();
		var items = new List<ArcItem>();
		
		// Build a value map for calculating recycle values
		var itemValueMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var rtItem in raidTheoryItems) {
			itemValueMap[rtItem.Id] = rtItem.Value;
		}
		
		// Convert RaidTheory items to ArcItem format
		foreach (var rtItem in raidTheoryItems) {
			var item = new ArcItem {
				Id = rtItem.Id,
				Name = rtItem.GetName(),
				ShortName = rtItem.GetName(),
				Width = 1, // Default, RaidTheory doesn't specify size
				Height = 1,
				Value = rtItem.Value,
				RecycleValue = rtItem.CalculateRecycleValue(itemValueMap),
				IsQuestItem = false, // Will be determined by other means
				IsBaseItem = false,
				IsRecyclable = rtItem.RecyclesInto != null && rtItem.RecyclesInto.Count > 0,
				IsCraftingItem = false,
				Rarity = rtItem.Rarity,
				Weight = rtItem.WeightKg,
				ImageLink = rtItem.ImageFilename,
				WikiLink = null, // RaidTheory doesn't include wiki links
				Category = rtItem.Type
			};
			
			// Convert recycles into to RecycleOutput format
			if (rtItem.RecyclesInto != null) {
				foreach (var (itemId, quantity) in rtItem.RecyclesInto) {
					item.RecycleOutputs.Add(new RecycleOutput {
						Id = itemId,
						Name = raidTheoryItems.FirstOrDefault(i => i.Id == itemId)?.GetName(),
						Quantity = quantity
					});
				}
			}
			
			items.Add(item);
		}
		
		return items;
	}

	private static string GetDataPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersItems.json");
	private static string GetRecycleOverridesPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersRecycleValues.json");
	private static string GetRecycleOutputsPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersRecycleOutputs.json");
	private static string GetCraftingOverridesPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersCraftingKeep.json");
	private static string GetCraftingUsagePath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersCraftingUsage.json");
	private static string GetCraftingWeightsPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersCraftingWeights.json");

	private static bool HasValidOverrideFile(string path) {
		try {
			if (!File.Exists(path)) return false;
			string json = File.ReadAllText(path);
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
			return doc.RootElement.EnumerateObject().Any();
		} catch {
			return false;
		}
	}

	private static void ApplyRecycleValueOverrides(List<ArcItem> items) {
		try {
			string overridePath = GetRecycleOverridesPath();
			if (!File.Exists(overridePath)) return;
			
			string json = File.ReadAllText(overridePath);
			var overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			if (overrides == null || overrides.Count == 0) return;

			foreach (var item in items) {
				if (overrides.TryGetValue(item.Id, out int recycleValue)) {
					item.RecycleValue = recycleValue;
				}
			}
		} catch {
			// Ignore override failures
		}
	}

	private static void ApplyRecycleOutputOverrides(List<ArcItem> items) {
		try {
			string overridePath = GetRecycleOutputsPath();
			if (!File.Exists(overridePath)) return;

			string json = File.ReadAllText(overridePath);
			var overrides = JsonSerializer.Deserialize<Dictionary<string, List<RecycleOutput>>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			if (overrides == null || overrides.Count == 0) return;

			foreach (var item in items) {
				if (overrides.TryGetValue(item.Id, out var outputs) && outputs != null) {
					item.RecycleOutputs = outputs;
				}
			}
		} catch {
			// Ignore override failures
		}
	}

	private static void ApplyCraftingUsageOverrides(List<ArcItem> items) {
		try {
			string overridePath = GetCraftingUsagePath();
			if (!File.Exists(overridePath)) return;

			string json = File.ReadAllText(overridePath);
			var overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			if (overrides == null || overrides.Count == 0) return;

			foreach (var item in items) {
				if (overrides.TryGetValue(item.Id, out int usedInCount)) {
					item.UsedInCount = Math.Max(item.UsedInCount, usedInCount);
					item.HasCraftingUsageData = true;
					if (usedInCount > 0) {
						item.IsCraftingItem = true;
						item.CraftingNote = $"Used in {usedInCount} recipes";
					}
				}
			}
		} catch {
			// Ignore override failures
		}
	}

	private static void ApplyCraftingKeepOverrides(List<ArcItem> items) {
		try {
			string overridePath = GetCraftingOverridesPath();
			if (!File.Exists(overridePath)) return;
			
			string json = File.ReadAllText(overridePath);
			var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			if (overrides == null || overrides.Count == 0) return;

			foreach (var item in items) {
				if (overrides.TryGetValue(item.Id, out string? note)) {
					item.IsCraftingItem = true;
					item.CraftingNote = note;
				}
			}
		} catch {
			// Ignore override failures
		}
	}

	private static void EnsureAuxData(List<ArcItem> items) {
		bool needsRecycle = !HasValidOverrideFile(GetRecycleOverridesPath());
		bool needsRecycleOutputs = !HasValidOverrideFile(GetRecycleOutputsPath());
		bool needsCrafting = !HasValidOverrideFile(GetCraftingOverridesPath());
		if (!needsRecycle && !needsRecycleOutputs && !needsCrafting) return;

		_ = Task.Run(async () => {
			try {
				var recycleMap = new Dictionary<string, int>();
				var recycleOutputsMap = new Dictionary<string, List<RecycleOutput>>();
				var craftingMap = new Dictionary<string, string>();
				bool anyUpdates = false;

				foreach (var item in items) {
					if (string.IsNullOrWhiteSpace(item.Id)) continue;

					var details = await TryFetchMetaforgeItemDetailsAsync(item.Id).ConfigureAwait(false);
					if (needsRecycle && details.totalRecycleValue > 0) {
						recycleMap[item.Id] = details.totalRecycleValue;
						item.RecycleValue = details.totalRecycleValue;
						anyUpdates = true;
					}
					if (needsRecycleOutputs && details.recycleOutputs.Count > 0) {
						recycleOutputsMap[item.Id] = details.recycleOutputs;
						item.RecycleOutputs = details.recycleOutputs;
						anyUpdates = true;
					}
					if (needsCrafting && details.usedInCount > 0) {
						string note = $"Used in {details.usedInCount} recipes";
						craftingMap[item.Id] = note;
						item.IsCraftingItem = true;
						item.CraftingNote = note;
						anyUpdates = true;
					}

					await Task.Delay(MetaforgeDelayMs).ConfigureAwait(false);
				}

				if (needsRecycle && recycleMap.Count > 0) {
					WriteOverrides(GetRecycleOverridesPath(), recycleMap);
				}
				if (needsRecycleOutputs && recycleOutputsMap.Count > 0) {
					WriteOverrides(GetRecycleOutputsPath(), recycleOutputsMap);
				}
				if (needsCrafting && craftingMap.Count > 0) {
					WriteOverrides(GetCraftingOverridesPath(), craftingMap);
				}
				if (anyUpdates) {
					AuxDataUpdated?.Invoke();
				}
			} catch {
				// Ignore fetch failures
			}
		});
	}

	private static void EnsureOverrideWatchers() {
		if (OverrideWatcher != null) return;
		try {
			string dir = Path.Combine(AppContext.BaseDirectory, "Resources");
			if (!Directory.Exists(dir)) return;

			OverrideWatcher = new FileSystemWatcher(dir) {
				Filter = "ArcRaiders*.json",
				IncludeSubdirectories = false,
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
			};

			OverrideWatcher.Changed += (_, e) => TryReloadOverrides(e.Name);
			OverrideWatcher.Created += (_, e) => TryReloadOverrides(e.Name);
			OverrideWatcher.Renamed += (_, e) => TryReloadOverrides(e.Name);
			OverrideWatcher.EnableRaisingEvents = true;
		} catch {
			// Ignore watcher failures
		}
	}

	private static void TryReloadOverrides(string? fileName) {
		if (string.IsNullOrWhiteSpace(fileName)) return;
		if (!IsOverrideFile(fileName)) return;

		lock (OverrideReloadLock) {
			var now = DateTime.UtcNow;
			if ((now - LastOverrideReloadUtc).TotalMilliseconds < 500) return;
			LastOverrideReloadUtc = now;
		}

		Task.Run(() => {
			try {
				lock (OverrideReloadLock) {
					var items = Items.Value;
					ApplyRecycleValueOverrides(items);
					ApplyRecycleOutputOverrides(items);
					ApplyCraftingUsageOverrides(items);
					ApplyCraftingKeepOverrides(items);
					if (fileName.Equals("ArcRaidersCraftingWeights.json", StringComparison.OrdinalIgnoreCase)) {
						CraftingWeights = null;
					}
				}
				AuxDataUpdated?.Invoke();
			} catch {
				// Ignore reload failures
			}
		});
	}

	private static bool IsOverrideFile(string fileName) {
		return fileName.Equals("ArcRaidersRecycleValues.json", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("ArcRaidersRecycleOutputs.json", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("ArcRaidersCraftingKeep.json", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("ArcRaidersCraftingUsage.json", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("ArcRaidersCraftingWeights.json", StringComparison.OrdinalIgnoreCase);
	}

	public static CraftingWeightsConfig GetCraftingWeights() {
		if (CraftingWeights != null) return CraftingWeights;
		try {
			string path = GetCraftingWeightsPath();
			if (!File.Exists(path)) {
				CraftingWeights = new CraftingWeightsConfig();
				return CraftingWeights;
			}

			string json = File.ReadAllText(path);
			var config = JsonSerializer.Deserialize<CraftingWeightsConfig>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			CraftingWeights = config ?? new CraftingWeightsConfig();
			return CraftingWeights;
		} catch {
			CraftingWeights = new CraftingWeightsConfig();
			return CraftingWeights;
		}
	}

	private static async Task<(int totalRecycleValue, int usedInCount, List<RecycleOutput> recycleOutputs)> TryFetchMetaforgeItemDetailsAsync(string itemId) {
		try {
			string url = $"https://metaforge.app/arc-raiders/database/item/{itemId}/__data.json";
			using var stream = await MetaforgeClient.GetStreamAsync(url).ConfigureAwait(false);
			using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
			if (!doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.GetArrayLength() < 3) {
				return (0, 0, new List<RecycleOutput>());
			}
			var dataElement = nodes[2].GetProperty("data");
			var data = dataElement.EnumerateArray().ToArray();
			var cache = new Dictionary<int, object?>();
			var root = ResolveNode(data, data[0], cache) as Dictionary<string, object?>;
			if (root == null) return (0, 0, new List<RecycleOutput>());

			int totalRecycleValue = 0;
			if (root.TryGetValue("totalRecycleValue", out var totalRecycleObj)) {
				TryConvertToInt(totalRecycleObj, out totalRecycleValue);
			}

			var recycleOutputs = ExtractRecycleOutputs(root);

			int usedInCount = 0;
			if (root.TryGetValue("item", out var itemObj) && itemObj is Dictionary<string, object?> itemDict) {
				if (itemDict.TryGetValue("used_in", out var usedInObj) && usedInObj is List<object?> usedInList) {
					usedInCount = usedInList.Count;
				}
			}

			return (totalRecycleValue, usedInCount, recycleOutputs);
		} catch (Exception ex) {
			Logger.LogDebug($"Recycle outputs fetch failed for {itemId}: {ex.Message}");
			return (0, 0, new List<RecycleOutput>());
		}
	}

	private static List<RecycleOutput> ExtractRecycleOutputs(Dictionary<string, object?> root) {
		var outputs = new List<RecycleOutput>();
		if (TryAddOutputs(root, "recycleComponentsDetails", outputs)) return outputs;
		if (root.TryGetValue("item", out var itemObj) && itemObj is Dictionary<string, object?> itemDict) {
			if (TryAddOutputs(itemDict, "recycle_components_details", outputs)) return outputs;
			TryAddOutputs(itemDict, "recycle_components", outputs);
		}
		return outputs;
	}

	private static bool TryAddOutputs(Dictionary<string, object?> source, string key, List<RecycleOutput> outputs) {
		if (!source.TryGetValue(key, out var detailsObj) || detailsObj == null) return false;
		if (detailsObj is List<object?> list) {
			foreach (var entry in list) {
				if (entry is Dictionary<string, object?> dict) {
					var output = ParseRecycleOutput(dict);
					if (output != null) outputs.Add(output);
				}
			}
			return outputs.Count > 0;
		}
		return false;
	}

	private static RecycleOutput? ParseRecycleOutput(Dictionary<string, object?> dict) {
		string? id = null;
		string? name = null;
		int quantity = 1;

		if (dict.TryGetValue("quantity", out var qtyObj) && qtyObj != null) {
			if (TryConvertToInt(qtyObj, out int parsedQuantity)) {
				quantity = Math.Max(1, parsedQuantity);
			}
		}

		if (dict.TryGetValue("item", out var itemObj) && itemObj is Dictionary<string, object?> itemDict) {
			if (itemDict.TryGetValue("id", out var idObj)) id = idObj?.ToString();
			if (itemDict.TryGetValue("name", out var nameObj)) name = nameObj?.ToString();
		} else {
			if (dict.TryGetValue("id", out var idObj)) id = idObj?.ToString();
			if (dict.TryGetValue("name", out var nameObj)) name = nameObj?.ToString();
		}

		if (string.IsNullOrWhiteSpace(id)) return null;
		return new RecycleOutput {
			Id = id ?? "",
			Name = name,
			Quantity = quantity
		};
	}

	private static bool TryConvertToInt(object? value, out int result) {
		result = 0;
		if (value == null) return false;
		switch (value) {
			case int i:
				result = i;
				return true;
			case long l:
				result = l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
				return true;
			case double d:
				result = d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)Math.Round(d);
				return true;
			case float f:
				result = f > int.MaxValue ? int.MaxValue : f < int.MinValue ? int.MinValue : (int)Math.Round(f);
				return true;
			case string s:
				if (int.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out int parsedInt)) {
					result = parsedInt;
					return true;
				}
				if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedDouble)) {
					result = parsedDouble > int.MaxValue ? int.MaxValue : parsedDouble < int.MinValue ? int.MinValue : (int)Math.Round(parsedDouble);
					return true;
				}
				return false;
			default:
				return false;
		}
	}

	private static object? ResolveNode(JsonElement[] data, JsonElement element, Dictionary<int, object?> cache) {
		switch (element.ValueKind) {
			case JsonValueKind.Number:
				if (element.TryGetInt32(out int index) && index >= 0 && index < data.Length) {
					if (cache.TryGetValue(index, out var cached)) return cached;
					cache[index] = null;
					var resolved = ResolveNode(data, data[index], cache);
					cache[index] = resolved;
					return resolved;
				}
				if (element.TryGetInt64(out long longValue)) return longValue;
				return element.GetDouble();
			case JsonValueKind.String:
				return element.GetString();
			case JsonValueKind.True:
			case JsonValueKind.False:
				return element.GetBoolean();
			case JsonValueKind.Object:
				var obj = new Dictionary<string, object?>();
				foreach (var prop in element.EnumerateObject()) {
					obj[prop.Name] = ResolveNode(data, prop.Value, cache);
				}
				return obj;
			case JsonValueKind.Array:
				var list = new List<object?>();
				foreach (var item in element.EnumerateArray()) {
					list.Add(ResolveNode(data, item, cache));
				}
				return list;
			case JsonValueKind.Null:
			case JsonValueKind.Undefined:
			default:
				return null;
		}
	}

	private static void WriteOverrides<T>(string path, Dictionary<string, T> map) {
		var ordered = map.OrderBy(kvp => kvp.Key).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
		string json = JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true });
		File.WriteAllText(path, json);
	}

	public static void EnsureRecycleOutputsFor(string itemId) {
		if (string.IsNullOrWhiteSpace(itemId)) return;
		lock (RecycleFetchLock) {
			if (PendingRecycleOutputFetches.Contains(itemId)) return;
			PendingRecycleOutputFetches.Add(itemId);
		}

		Logger.LogDebug($"Recycle outputs fetch queued for {itemId}");

		_ = Task.Run(async () => {
			try {
				Logger.LogDebug($"Recycle outputs fetch started for {itemId}");
				var details = await TryFetchMetaforgeItemDetailsAsync(itemId).ConfigureAwait(false);
				if (details.recycleOutputs.Count == 0) {
					Logger.LogDebug($"Recycle outputs fetch empty for {itemId}");
					return;
				}

				lock (RecycleFetchLock) {
					var item = Items.Value.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
					if (item != null) {
						item.RecycleOutputs = details.recycleOutputs;
					}
				}

				MergeRecycleOutputs(itemId, details.recycleOutputs);
				Logger.LogDebug($"Recycle outputs fetch success for {itemId}: {details.recycleOutputs.Count} outputs");
				AuxDataUpdated?.Invoke();
			} catch {
				// Ignore fetch failures
			} finally {
				lock (RecycleFetchLock) {
					PendingRecycleOutputFetches.Remove(itemId);
				}
			}
		});
	}

	public static void EnsureCraftingUsageFor(string itemId) {
		if (string.IsNullOrWhiteSpace(itemId)) return;
		try {
			var existingItem = Items.Value.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
			if (existingItem != null && existingItem.HasCraftingUsageData) return;
		} catch {
			// Ignore lookup failures
		}
		string key = $"craft:{itemId}";
		lock (RecycleFetchLock) {
			if (PendingRecycleOutputFetches.Contains(key)) return;
			PendingRecycleOutputFetches.Add(key);
		}

		Logger.LogDebug($"Crafting usage fetch queued for {itemId}");

		_ = Task.Run(async () => {
			try {
				Logger.LogDebug($"Crafting usage fetch started for {itemId}");
				var details = await TryFetchMetaforgeItemDetailsAsync(itemId).ConfigureAwait(false);
				if (details.usedInCount <= 0) {
					Logger.LogDebug($"Crafting usage fetch empty for {itemId}");
					lock (RecycleFetchLock) {
						var item = Items.Value.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
						if (item != null) {
							item.UsedInCount = Math.Max(item.UsedInCount, 0);
							item.HasCraftingUsageData = true;
						}
					}
					MergeCraftingUsage(itemId, 0);
					AuxDataUpdated?.Invoke();
					return;
				}

				lock (RecycleFetchLock) {
					var item = Items.Value.FirstOrDefault(i => i.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
					if (item != null) {
						item.UsedInCount = Math.Max(item.UsedInCount, details.usedInCount);
						item.IsCraftingItem = true;
						item.CraftingNote = $"Used in {item.UsedInCount} recipes";
						item.HasCraftingUsageData = true;
					}
				}

				MergeCraftingUsage(itemId, details.usedInCount);
				Logger.LogDebug($"Crafting usage fetch success for {itemId}: {details.usedInCount}");
				AuxDataUpdated?.Invoke();
			} catch {
				// Ignore fetch failures
			} finally {
				lock (RecycleFetchLock) {
					PendingRecycleOutputFetches.Remove(key);
				}
			}
		});
	}

	private static void MergeCraftingUsage(string itemId, int usedInCount) {
		try {
			string path = GetCraftingUsagePath();
			var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			if (File.Exists(path)) {
				string json = File.ReadAllText(path);
				var existing = JsonSerializer.Deserialize<Dictionary<string, int>>(json, new JsonSerializerOptions {
					PropertyNameCaseInsensitive = true
				});
				if (existing != null) {
					foreach (var kvp in existing) {
						map[kvp.Key] = kvp.Value;
					}
				}
			}

			map[itemId] = usedInCount;
			WriteOverrides(path, map);
		} catch {
			// Ignore merge failures
		}
	}

	private static void MergeRecycleOutputs(string itemId, List<RecycleOutput> outputs) {
		try {
			string path = GetRecycleOutputsPath();
			var map = new Dictionary<string, List<RecycleOutput>>(StringComparer.OrdinalIgnoreCase);
			if (File.Exists(path)) {
				string json = File.ReadAllText(path);
				var existing = JsonSerializer.Deserialize<Dictionary<string, List<RecycleOutput>>>(json, new JsonSerializerOptions {
					PropertyNameCaseInsensitive = true
				});
				if (existing != null) {
					foreach (var kvp in existing) {
						map[kvp.Key] = kvp.Value ?? new List<RecycleOutput>();
					}
				}
			}

			map[itemId] = outputs;
			WriteOverrides(path, map);
		} catch {
			// Ignore merge failures
		}
	}
	
	/// <summary>
	/// Get all Arc Raiders items
	/// </summary>
	public static ArcItem[] GetItems() => Items.Value.ToArray();
	
	/// <summary>
	/// Get item by ID
	/// </summary>
	public static ArcItem? GetItemById(string id) => Items.Value.FirstOrDefault(i => i.Id == id);
	
	/// <summary>
	/// Get item by name
	/// </summary>
	public static ArcItem? GetItemByName(string name) => Items.Value.FirstOrDefault(i => 
		i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || 
		i.ShortName.Equals(name, StringComparison.OrdinalIgnoreCase));
	
	/// <summary>
	/// Get items by category
	/// </summary>
	public static ArcItem[] GetItemsByCategory(string category) => 
		Items.Value.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
	
	/// <summary>
	/// Get all quest items
	/// </summary>
	public static ArcItem[] GetQuestItems() => Items.Value.Where(i => i.IsQuestItem).ToArray();
	
	/// <summary>
	/// Get all base building items
	/// </summary>
	public static ArcItem[] GetBaseItems() => Items.Value.Where(i => i.IsBaseItem).ToArray();
	
	/// <summary>
	/// Get all trades from RaidTheory data
	/// </summary>
	public static List<RaidTheoryDataSource.RaidTheoryTrade> GetTrades() {
		if (!RaidTheoryDataSource.IsDataAvailable()) {
			Logger.LogWarning("RaidTheory data not available for trades");
			return new List<RaidTheoryDataSource.RaidTheoryTrade>();
		}
		return RaidTheoryDataSource.LoadTrades();
	}
	
	/// <summary>
	/// Get all skill nodes from RaidTheory data
	/// </summary>
	public static List<RaidTheoryDataSource.RaidTheorySkillNode> GetSkillNodes() {
		if (!RaidTheoryDataSource.IsDataAvailable()) {
			Logger.LogWarning("RaidTheory data not available for skill nodes");
			return new List<RaidTheoryDataSource.RaidTheorySkillNode>();
		}
		return RaidTheoryDataSource.LoadSkillNodes();
	}
	
	/// <summary>
	/// Get trades for a specific trader
	/// </summary>
	public static List<RaidTheoryDataSource.RaidTheoryTrade> GetTradesByTrader(string traderName) {
		return GetTrades().Where(t => t.Trader.Equals(traderName, StringComparison.OrdinalIgnoreCase)).ToList();
	}
	
	/// <summary>
	/// Get skill nodes by category
	/// </summary>
	public static List<RaidTheoryDataSource.RaidTheorySkillNode> GetSkillNodesByCategory(string category) {
		return GetSkillNodes().Where(s => s.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
	}
	
	/// <summary>
	/// Get all hideout modules from RaidTheory data
	/// </summary>
	public static List<RaidTheoryDataSource.RaidTheoryHideoutModule> GetHideoutModules() {
		if (!RaidTheoryDataSource.IsDataAvailable()) {
			Logger.LogWarning("RaidTheory data not available for hideout modules");
			return new List<RaidTheoryDataSource.RaidTheoryHideoutModule>();
		}
		return RaidTheoryDataSource.LoadHideoutModules();
	}
	
	/// <summary>
	/// Get hideout module by ID
	/// </summary>
	public static RaidTheoryDataSource.RaidTheoryHideoutModule? GetHideoutModuleById(string id) {
		return GetHideoutModules().FirstOrDefault(h => h.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
	}
	
	/// <summary>
	/// Force refresh RaidTheory data from repository
	/// </summary>
	public static async Task<bool> RefreshRaidTheoryDataAsync() {
		bool success = await RaidTheoryDataSource.DownloadDataAsync().ConfigureAwait(false);
		if (success) {
			// Trigger reload by clearing the lazy value
			// Note: This requires restarting the application for full effect
			Logger.LogInfo("RaidTheory data refreshed. Restart application to load new data.");
			AuxDataUpdated?.Invoke();
		}
		return success;
	}
}
