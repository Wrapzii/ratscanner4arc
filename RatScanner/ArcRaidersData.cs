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
		public bool IsQuestItem { get; set; } = false;
		public bool IsBaseItem { get; set; } = false; // For base building
		public bool IsRecyclable { get; set; } = false;
		public bool IsCraftingItem { get; set; } = false;
		public string? CraftingNote { get; set; }
		public string Rarity { get; set; } = "";
		public double Weight { get; set; } = 0;
		public string? ImageLink { get; set; }
		public string? WikiLink { get; set; }
		public string Category { get; set; } = "General";
		
		// Calculated property
		public int ValuePerSlot => Value / Math.Max(1, Width * Height);
	}
	
	/// <summary>
	/// Hardcoded Arc Raiders items database loaded from a generated JSON file.
	/// Run Tools/ArcRaidersItemsScraper.ps1 to refresh the data.
	/// </summary>
	private static readonly Lazy<List<ArcItem>> Items = new(LoadItems);
	private static readonly HttpClient MetaforgeClient = new();
	private const int MetaforgeDelayMs = 150;

	private static List<ArcItem> LoadItems() {
		try {
			string dataPath = GetDataPath();
			if (!File.Exists(dataPath)) {
				return new List<ArcItem>();
			}

			string json = File.ReadAllText(dataPath);
			var items = JsonSerializer.Deserialize<List<ArcItem>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			
			if (items == null) return new List<ArcItem>();

			ApplyRecycleValueOverrides(items);
			ApplyCraftingKeepOverrides(items);
			EnsureAuxData(items);
			return items;
		} catch {
			return new List<ArcItem>();
		}
	}

	private static string GetDataPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersItems.json");
	private static string GetRecycleOverridesPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersRecycleValues.json");
	private static string GetCraftingOverridesPath() => Path.Combine(AppContext.BaseDirectory, "Resources", "ArcRaidersCraftingKeep.json");

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
		bool needsCrafting = !HasValidOverrideFile(GetCraftingOverridesPath());
		if (!needsRecycle && !needsCrafting) return;

		_ = Task.Run(async () => {
			try {
				var recycleMap = new Dictionary<string, int>();
				var craftingMap = new Dictionary<string, string>();

				foreach (var item in items) {
					if (string.IsNullOrWhiteSpace(item.Id)) continue;

					var details = await TryFetchMetaforgeItemDetailsAsync(item.Id).ConfigureAwait(false);
					if (needsRecycle && details.totalRecycleValue > 0) {
						recycleMap[item.Id] = details.totalRecycleValue;
						item.RecycleValue = details.totalRecycleValue;
					}
					if (needsCrafting && details.usedInCount > 0) {
						string note = $"Used in {details.usedInCount} recipes";
						craftingMap[item.Id] = note;
						item.IsCraftingItem = true;
						item.CraftingNote = note;
					}

					await Task.Delay(MetaforgeDelayMs).ConfigureAwait(false);
				}

				if (needsRecycle && recycleMap.Count > 0) {
					WriteOverrides(GetRecycleOverridesPath(), recycleMap);
				}
				if (needsCrafting && craftingMap.Count > 0) {
					WriteOverrides(GetCraftingOverridesPath(), craftingMap);
				}
			} catch {
				// Ignore fetch failures
			}
		});
	}

	private static async Task<(int totalRecycleValue, int usedInCount)> TryFetchMetaforgeItemDetailsAsync(string itemId) {
		try {
			string url = $"https://metaforge.app/arc-raiders/database/item/{itemId}/__data.json";
			using var stream = await MetaforgeClient.GetStreamAsync(url).ConfigureAwait(false);
			using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
			if (!doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.GetArrayLength() < 3) {
				return (0, 0);
			}
			var dataElement = nodes[2].GetProperty("data");
			var data = dataElement.EnumerateArray().ToArray();
			var cache = new Dictionary<int, object?>();
			var root = ResolveNode(data, data[0], cache) as Dictionary<string, object?>;
			if (root == null) return (0, 0);

			int totalRecycleValue = 0;
			if (root.TryGetValue("totalRecycleValue", out var totalRecycleObj)) {
				totalRecycleValue = Convert.ToInt32(totalRecycleObj);
			}

			int usedInCount = 0;
			if (root.TryGetValue("item", out var itemObj) && itemObj is Dictionary<string, object?> itemDict) {
				if (itemDict.TryGetValue("used_in", out var usedInObj) && usedInObj is List<object?> usedInList) {
					usedInCount = usedInList.Count;
				}
			}

			return (totalRecycleValue, usedInCount);
		} catch {
			return (0, 0);
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
}
