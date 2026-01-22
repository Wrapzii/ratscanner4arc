using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RatScanner;

/// <summary>
/// Manages data synchronization from the RaidTheory/arcraiders-data GitHub repository
/// Source: https://github.com/RaidTheory/arcraiders-data
/// </summary>
public static class RaidTheoryDataSource {
	private static readonly HttpClient HttpClient = new();
	private const string DataRepoUrl = "https://github.com/RaidTheory/arcraiders-data/archive/refs/heads/main.zip";
	private const string DataRepoBranch = "main";
	
	private static string CachePath => Path.Combine(RatConfig.Paths.CacheDir, "RaidTheoryData");
	private static string DataPath => Path.Combine(CachePath, "arcraiders-data-main");
	private static string LastUpdatePath => Path.Combine(CachePath, "last_update.txt");
	
	/// <summary>
	/// Check if data is available locally
	/// </summary>
	public static bool IsDataAvailable() {
		return Directory.Exists(DataPath) && 
		       Directory.Exists(Path.Combine(DataPath, "items")) &&
		       File.Exists(Path.Combine(DataPath, "trades.json"));
	}
	
	/// <summary>
	/// Get time since last data update
	/// </summary>
	public static TimeSpan? GetTimeSinceLastUpdate() {
		try {
			if (!File.Exists(LastUpdatePath)) return null;
			string timestampStr = File.ReadAllText(LastUpdatePath).Trim();
			if (DateTime.TryParse(timestampStr, out DateTime lastUpdate)) {
				return DateTime.UtcNow - lastUpdate;
			}
		} catch (Exception ex) {
			Logger.LogDebug($"Failed to read last update timestamp: {ex.Message}");
		}
		return null;
	}
	
	/// <summary>
	/// Download and extract the latest data from RaidTheory repository
	/// </summary>
	public static async Task<bool> DownloadDataAsync() {
		try {
			Logger.LogInfo("Downloading RaidTheory arcraiders-data repository...");
			
			// Create cache directory
			Directory.CreateDirectory(CachePath);
			
			// Download zip file
			string zipPath = Path.Combine(CachePath, "arcraiders-data.zip");
			using (var response = await HttpClient.GetAsync(DataRepoUrl).ConfigureAwait(false)) {
				response.EnsureSuccessStatusCode();
				await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
					await response.Content.CopyToAsync(fs).ConfigureAwait(false);
				}
			}
			
			Logger.LogInfo("Extracting data...");
			
			// Remove old data if exists
			if (Directory.Exists(DataPath)) {
				Directory.Delete(DataPath, true);
			}
			
			// Extract zip
			ZipFile.ExtractToDirectory(zipPath, CachePath);
			
			// Clean up zip file
			File.Delete(zipPath);
			
			// Save update timestamp
			File.WriteAllText(LastUpdatePath, DateTime.UtcNow.ToString("O"));
			
			Logger.LogInfo("RaidTheory data downloaded successfully");
			return true;
		} catch (Exception ex) {
			Logger.LogError($"Failed to download RaidTheory data: {ex.Message}");
			return false;
		}
	}
	
	/// <summary>
	/// Ensure data is available, download if needed
	/// </summary>
	public static async Task EnsureDataAsync() {
		if (IsDataAvailable()) {
			var timeSinceUpdate = GetTimeSinceLastUpdate();
			// Only auto-update if data is older than 24 hours
			if (timeSinceUpdate.HasValue && timeSinceUpdate.Value.TotalHours < 24) {
				return;
			}
			
			Logger.LogInfo("Data is outdated, checking for updates...");
		}
		
		await DownloadDataAsync().ConfigureAwait(false);
	}
	
	/// <summary>
	/// Load all items from RaidTheory data
	/// </summary>
	public static List<RaidTheoryItem> LoadItems() {
		var items = new List<RaidTheoryItem>();
		
		try {
			string itemsDir = Path.Combine(DataPath, "items");
			if (!Directory.Exists(itemsDir)) {
				Logger.LogWarning("RaidTheory items directory not found");
				return items;
			}
			
			foreach (string file in Directory.GetFiles(itemsDir, "*.json")) {
				try {
					string json = File.ReadAllText(file);
					var item = JsonSerializer.Deserialize<RaidTheoryItem>(json, new JsonSerializerOptions {
						PropertyNameCaseInsensitive = true
					});
					if (item != null) {
						items.Add(item);
					}
				} catch (Exception ex) {
					Logger.LogWarning($"Failed to parse item file {Path.GetFileName(file)}: {ex.Message}");
				}
			}
			
			Logger.LogInfo($"Loaded {items.Count} items from RaidTheory data");
		} catch (Exception ex) {
			Logger.LogError($"Failed to load items from RaidTheory data: {ex.Message}");
		}
		
		return items;
	}
	
	/// <summary>
	/// Load all trades from RaidTheory data
	/// </summary>
	public static List<RaidTheoryTrade> LoadTrades() {
		var trades = new List<RaidTheoryTrade>();
		
		try {
			string tradesFile = Path.Combine(DataPath, "trades.json");
			if (!File.Exists(tradesFile)) {
				Logger.LogWarning("RaidTheory trades.json not found");
				return trades;
			}
			
			string json = File.ReadAllText(tradesFile);
			trades = JsonSerializer.Deserialize<List<RaidTheoryTrade>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			}) ?? new List<RaidTheoryTrade>();
			
			Logger.LogInfo($"Loaded {trades.Count} trades from RaidTheory data");
		} catch (Exception ex) {
			Logger.LogError($"Failed to load trades from RaidTheory data: {ex.Message}");
		}
		
		return trades;
	}
	
	/// <summary>
	/// Load all skill nodes from RaidTheory data
	/// </summary>
	public static List<RaidTheorySkillNode> LoadSkillNodes() {
		var skillNodes = new List<RaidTheorySkillNode>();
		
		try {
			string skillNodesFile = Path.Combine(DataPath, "skillNodes.json");
			if (!File.Exists(skillNodesFile)) {
				Logger.LogWarning("RaidTheory skillNodes.json not found");
				return skillNodes;
			}
			
			string json = File.ReadAllText(skillNodesFile);
			skillNodes = JsonSerializer.Deserialize<List<RaidTheorySkillNode>>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			}) ?? new List<RaidTheorySkillNode>();
			
			Logger.LogInfo($"Loaded {skillNodes.Count} skill nodes from RaidTheory data");
		} catch (Exception ex) {
			Logger.LogError($"Failed to load skill nodes from RaidTheory data: {ex.Message}");
		}
		
		return skillNodes;
	}
	
	/// <summary>
	/// RaidTheory item data structure
	/// </summary>
	public class RaidTheoryItem {
		public string Id { get; set; } = "";
		public Dictionary<string, string>? Name { get; set; }
		public Dictionary<string, string>? Description { get; set; }
		public string Type { get; set; } = "";
		public string Rarity { get; set; } = "";
		public int Value { get; set; } = 0;
		public double WeightKg { get; set; } = 0;
		public int StackSize { get; set; } = 1;
		public Dictionary<string, int>? RecyclesInto { get; set; }
		public Dictionary<string, int>? SalvagesInto { get; set; }
		public string? ImageFilename { get; set; }
		public string? UpdatedAt { get; set; }
		
		/// <summary>
		/// Get name in English
		/// </summary>
		public string GetName() => Name?.GetValueOrDefault("en", Id) ?? Id;
		
		/// <summary>
		/// Get description in English
		/// </summary>
		public string? GetDescription() => Description?.GetValueOrDefault("en");
		
		/// <summary>
		/// Calculate total recycle value based on recyclesInto items
		/// </summary>
		public int CalculateRecycleValue(Dictionary<string, int> itemValues) {
			if (RecyclesInto == null || RecyclesInto.Count == 0) return 0;
			
			int total = 0;
			foreach (var (itemId, quantity) in RecyclesInto) {
				if (itemValues.TryGetValue(itemId, out int itemValue)) {
					total += itemValue * quantity;
				}
			}
			return total;
		}
	}
	
	/// <summary>
	/// RaidTheory trade data structure
	/// </summary>
	public class RaidTheoryTrade {
		public string Trader { get; set; } = "";
		public string ItemId { get; set; } = "";
		public int Quantity { get; set; } = 1;
		public TradeCost? Cost { get; set; }
		public int? DailyLimit { get; set; }
		
		public class TradeCost {
			public string ItemId { get; set; } = "";
			public int Quantity { get; set; } = 1;
		}
	}
	
	/// <summary>
	/// RaidTheory skill node data structure
	/// </summary>
	public class RaidTheorySkillNode {
		public string Id { get; set; } = "";
		public Dictionary<string, string>? Name { get; set; }
		public string Category { get; set; } = "";
		public bool IsMajor { get; set; } = false;
		public Dictionary<string, string>? Description { get; set; }
		public int MaxPoints { get; set; } = 1;
		public Dictionary<string, string>? ImpactedSkill { get; set; }
		public List<object>? KnownValue { get; set; }
		public SkillNodePosition? Position { get; set; }
		public List<string>? PrerequisiteNodeIds { get; set; }
		public string? IconName { get; set; }
		
		public class SkillNodePosition {
			public int X { get; set; }
			public int Y { get; set; }
		}
		
		/// <summary>
		/// Get name in English
		/// </summary>
		public string GetName() => Name?.GetValueOrDefault("en", Id) ?? Id;
		
		/// <summary>
		/// Get description in English
		/// </summary>
		public string? GetDescription() => Description?.GetValueOrDefault("en");
	}
	
	/// <summary>
	/// RaidTheory hideout module data structure
	/// </summary>
	public class RaidTheoryHideoutModule {
		public string Id { get; set; } = "";
		public Dictionary<string, string>? Name { get; set; }
		public int MaxLevel { get; set; } = 1;
		public List<HideoutLevel>? Levels { get; set; }
		
		public class HideoutLevel {
			public string? Description { get; set; }
			public int Level { get; set; } = 1;
			public List<string>? OtherRequirements { get; set; }
			public List<string>? RequirementItemIds { get; set; }
		}
		
		/// <summary>
		/// Get name in English
		/// </summary>
		public string GetName() => Name?.GetValueOrDefault("en", Id) ?? Id;
	}
	
	/// <summary>
	/// Load all hideout modules from RaidTheory data
	/// </summary>
	public static List<RaidTheoryHideoutModule> LoadHideoutModules() {
		var modules = new List<RaidTheoryHideoutModule>();
		
		try {
			string hideoutDir = Path.Combine(DataPath, "hideout");
			if (!Directory.Exists(hideoutDir)) {
				Logger.LogWarning("RaidTheory hideout directory not found");
				return modules;
			}
			
			foreach (string file in Directory.GetFiles(hideoutDir, "*.json")) {
				try {
					string json = File.ReadAllText(file);
					var module = JsonSerializer.Deserialize<RaidTheoryHideoutModule>(json, new JsonSerializerOptions {
						PropertyNameCaseInsensitive = true
					});
					if (module != null) {
						modules.Add(module);
					}
				} catch (Exception ex) {
					Logger.LogWarning($"Failed to parse hideout module file {Path.GetFileName(file)}: {ex.Message}");
				}
			}
			
			Logger.LogInfo($"Loaded {modules.Count} hideout modules from RaidTheory data");
		} catch (Exception ex) {
			Logger.LogError($"Failed to load hideout modules from RaidTheory data: {ex.Message}");
		}
		
		return modules;
	}
}
