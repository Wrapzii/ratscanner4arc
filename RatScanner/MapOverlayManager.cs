using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RatScanner;

/// <summary>
/// Map overlay system for displaying minimap and interactive map
/// </summary>
public class MapOverlayManager {
	public static MapOverlayManager Instance { get; } = new();
	
	private MapOverlayManager() {
		LoadLootSpawnData();
	}
	
	public class MapPosition {
		public double X { get; set; }
		public double Y { get; set; }
		public string MapId { get; set; } = "";
	}
	
	public class LootSpawn {
		public double X { get; set; }
		public double Y { get; set; }
		public string MapId { get; set; } = "";
		public string Category { get; set; } = "";
		public string ItemType { get; set; } = "";
		public string Rarity { get; set; } = "";
	}
	
	private MapPosition? _currentPosition;
	private string? _currentMapId;
	private string? _currentMapNameOverride;
	private string? _currentMapImageOverride;
	private readonly List<LootSpawn> _lootSpawns = new();
	private bool _isInRaid;
	
	/// <summary>
	/// Get current player position (if detected)
	/// </summary>
	public MapPosition? GetCurrentPosition() => _currentPosition;
	
	/// <summary>
	/// Whether the player is currently in-raid (based on UI detection)
	/// </summary>
	public bool IsInRaid => _isInRaid;
	
	/// <summary>
	/// Update player position based on detection
	/// </summary>
	public void UpdatePosition(double x, double y, string mapId, string? mapName = null, string? mapImageUrl = null) {
		_currentPosition = new MapPosition { X = x, Y = y, MapId = mapId };
		_currentMapId = mapId;
		_currentMapNameOverride = mapName;
		_currentMapImageOverride = mapImageUrl;
		Logger.LogDebug($"Position updated: ({x}, {y}) on {mapId}");
	}
	
	/// <summary>
	/// Clear map position (used when leaving a match)
	/// </summary>
	public void ClearPosition() {
		_currentPosition = null;
		_currentMapId = null;
		_currentMapNameOverride = null;
		_currentMapImageOverride = null;
		Logger.LogDebug("Map position cleared");
	}
	
	/// <summary>
	/// Update in-raid status for overlay visibility
	/// </summary>
	public void SetRaidState(bool isInRaid) {
		_isInRaid = isInRaid;
	}
	
	/// <summary>
	/// Get the current map info
	/// </summary>
	public RaidTheoryDataSource.RaidTheoryMap? GetCurrentMap() {
		if (string.IsNullOrEmpty(_currentMapId)) return null;
		return ArcRaidersData.GetMapById(_currentMapId);
	}
	
	/// <summary>
	/// Generate minimap data for overlay
	/// </summary>
	public MinimapData? GenerateMinimapData() {
		if (_currentPosition == null || _currentMapId == null) return null;
		
		var map = GetCurrentMap();
		if (map == null && string.IsNullOrWhiteSpace(_currentMapNameOverride)) return null;
		
		return new MinimapData {
			MapId = _currentMapId,
			MapName = map?.GetName() ?? _currentMapNameOverride ?? _currentMapId ?? "Unknown",
			PlayerX = _currentPosition.X,
			PlayerY = _currentPosition.Y,
			MapImageUrl = ResolveMapImageUrl(map?.Image ?? _currentMapImageOverride),
			NearbyLoot = GetNearbyLoot(_currentPosition.X, _currentPosition.Y, RatConfig.Map.NearbyLootRadius),
			ActiveQuests = GetActiveQuestInfo()
		};
	}
	
	/// <summary>
	/// Get active quest information for display
	/// </summary>
	private List<ActiveQuestInfo> GetActiveQuestInfo() {
		var quests = new List<ActiveQuestInfo>();
		var state = PlayerStateManager.GetState();
		
		foreach (var questId in state.ActiveQuests) {
			var quest = ArcRaidersData.GetQuestById(questId);
			if (quest != null) {
				quests.Add(new ActiveQuestInfo {
					QuestId = questId,
					QuestName = quest.GetName(),
					Objectives = new List<QuestObjective>() // Will be populated from screen detection
				});
			}
		}
		
		return quests;
	}
	
	/// <summary>
	/// Generate full interactive map data
	/// </summary>
	public InteractiveMapData GenerateInteractiveMapData(string mapId) {
		var map = ArcRaidersData.GetMapById(mapId);
		if (map == null) {
			return new InteractiveMapData {
				MapId = mapId,
				MapName = _currentMapNameOverride ?? mapId,
				MapImageUrl = ResolveMapImageUrl(_currentMapImageOverride),
				PlayerPosition = _currentMapId == mapId ? _currentPosition : null,
				LootSpawns = _lootSpawns.Where(l => l.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase)).ToList(),
				QuestLocations = GetQuestLocationsForMap(mapId)
			};
		}
		
		return new InteractiveMapData {
			MapId = mapId,
			MapName = map.GetName(),
			MapImageUrl = ResolveMapImageUrl(map.Image),
			PlayerPosition = _currentMapId == mapId ? _currentPosition : null,
			LootSpawns = _lootSpawns.Where(l => l.MapId.Equals(mapId, StringComparison.OrdinalIgnoreCase)).ToList(),
			QuestLocations = GetQuestLocationsForMap(mapId)
		};
	}

	private static string? ResolveMapImageUrl(string? imagePath) {
		if (string.IsNullOrWhiteSpace(imagePath)) return null;

		if (Uri.TryCreate(imagePath, UriKind.Absolute, out var absoluteUri)) {
			if (absoluteUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
			    || absoluteUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) {
				string fileName = Path.GetFileName(absoluteUri.LocalPath);
				if (!string.IsNullOrWhiteSpace(fileName)) {
					string relPath = Path.Combine("maps", fileName);
					string localCandidate = Path.Combine(RatConfig.Paths.Data, relPath);
					if (!File.Exists(localCandidate)) {
						TryCopyFromRaidTheoryCache(fileName, relPath);
					}
					if (File.Exists(localCandidate)) {
						return ToLocalDataUrl(relPath);
					}
				}
				return imagePath;
			}
		}

		if (imagePath.StartsWith("https://local.data/", StringComparison.OrdinalIgnoreCase)
		    || imagePath.StartsWith("http://local.data/", StringComparison.OrdinalIgnoreCase)) {
			return imagePath;
		}

		if (imagePath.StartsWith("local.data/", StringComparison.OrdinalIgnoreCase)) {
			return "https://" + imagePath;
		}

		if (Path.IsPathRooted(imagePath) && File.Exists(imagePath)) {
			string? mapped = TryMapIntoDataFolder(imagePath);
			if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
		}

		string dataCandidate = Path.Combine(RatConfig.Paths.Data, imagePath);
		if (File.Exists(dataCandidate)) {
			return ToLocalDataUrl(imagePath);
		}

		string cacheRoot = Path.Combine(RatConfig.Paths.CacheDir, "RaidTheoryData", "arcraiders-data-main");
		string cacheCandidate = Path.Combine(cacheRoot, imagePath);
		if (File.Exists(cacheCandidate)) {
			string? mapped = CopyToDataFolder(cacheCandidate, imagePath);
			if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
		}

		// Fall back to direct path if nothing else worked
		return imagePath;
	}

	private static void TryCopyFromRaidTheoryCache(string fileName, string relativeTargetPath) {
		try {
			string cacheMapPath = Path.Combine(RatConfig.Paths.CacheDir, "RaidTheoryData", "arcraiders-data-main", "images", "maps", fileName);
			if (!File.Exists(cacheMapPath)) return;
			CopyToDataFolder(cacheMapPath, relativeTargetPath);
		} catch {
			// ignore
		}
	}

	private static string? TryMapIntoDataFolder(string absolutePath) {
		try {
			if (absolutePath.StartsWith(RatConfig.Paths.Data, StringComparison.OrdinalIgnoreCase)) {
				string relWithinData = Path.GetRelativePath(RatConfig.Paths.Data, absolutePath);
				return ToLocalDataUrl(relWithinData);
			}

			string fileName = Path.GetFileName(absolutePath);
			if (string.IsNullOrWhiteSpace(fileName)) return null;
			string relTarget = Path.Combine("maps", fileName);
			return CopyToDataFolder(absolutePath, relTarget);
		} catch {
			return null;
		}
	}

	private static string? CopyToDataFolder(string sourcePath, string relativeTargetPath) {
		try {
			string destPath = Path.Combine(RatConfig.Paths.Data, relativeTargetPath);
			Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? RatConfig.Paths.Data);
			File.Copy(sourcePath, destPath, true);
			return ToLocalDataUrl(relativeTargetPath);
		} catch {
			return null;
		}
	}

	private static string ToLocalDataUrl(string relativePath) {
		string normalized = relativePath.Replace('\\', '/').TrimStart('/');
		return "https://local.data/" + normalized;
	}
	
	private List<LootSpawn> GetNearbyLoot(double x, double y, double radius) {
		// Calculate distance and return nearby loot
		return _lootSpawns.Where(loot => {
			if (_currentMapId != null && !loot.MapId.Equals(_currentMapId, StringComparison.OrdinalIgnoreCase)) return false;
			if (!IsCategoryEnabled(loot.Category)) return false;
			double dx = loot.X - x;
			double dy = loot.Y - y;
			double distance = Math.Sqrt(dx * dx + dy * dy);
			return distance <= radius;
		}).ToList();
	}
	
	private List<QuestLocation> GetQuestLocationsForMap(string mapId) {
		var locations = new List<QuestLocation>();
		var activeQuests = PlayerStateManager.GetState().ActiveQuests;
		
		// TODO: Parse quest objectives to extract location information
		// For now, return empty list
		
		return locations;
	}
	
	/// <summary>
	/// Load loot spawn data (would come from RaidTheory or custom data)
	/// </summary>
	public void LoadLootSpawnData() {
		_lootSpawns.Clear();

		try {
			string mapDataDir = Path.Combine(RatConfig.Paths.Data, "maps");
			if (!Directory.Exists(mapDataDir)) {
				Logger.LogWarning($"Map data directory not found: {mapDataDir}");
				return;
			}

			var allPoints = new List<ArcTrackerPoint>();
			foreach (string file in Directory.GetFiles(mapDataDir, "*_map.txt")) {
				try {
					string json = File.ReadAllText(file);
					var points = JsonSerializer.Deserialize<List<ArcTrackerPoint>>(json, new JsonSerializerOptions {
						PropertyNameCaseInsensitive = true
					});
					if (points != null && points.Count > 0) {
						allPoints.AddRange(points);
					}
				} catch (Exception ex) {
					Logger.LogWarning($"Failed to parse map data file {Path.GetFileName(file)}: {ex.Message}");
				}
			}

			if (allPoints.Count == 0) {
				Logger.LogInfo("No map overlay points found");
				return;
			}

			var pointsByMap = allPoints
				.Where(p => !string.IsNullOrWhiteSpace(p.MapId))
				.GroupBy(p => NormalizeMapId(p.MapId))
				.ToList();

			foreach (var group in pointsByMap) {
				string mapId = group.Key;
				var mapPoints = group.ToList();
				double minLat = mapPoints.Min(p => p.Lat);
				double maxLat = mapPoints.Max(p => p.Lat);
				double minLng = mapPoints.Min(p => p.Lng);
				double maxLng = mapPoints.Max(p => p.Lng);

				double latRange = maxLat - minLat;
				double lngRange = maxLng - minLng;
				if (latRange <= 0 || lngRange <= 0) continue;

				foreach (var point in mapPoints) {
					double xPercent = ((point.Lng - minLng) / lngRange) * 100.0;
					double yPercent = ((point.Lat - minLat) / latRange) * 100.0;
					xPercent = ClampPercent(xPercent);
					yPercent = ClampPercent(yPercent);

					_lootSpawns.Add(new LootSpawn {
						MapId = mapId,
						X = xPercent,
						Y = yPercent,
						Category = point.Category?.Trim() ?? "",
						ItemType = BuildItemType(point),
						Rarity = MapRarity(point)
					});
				}
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Failed to load loot spawn data: {ex.Message}");
		}

		Logger.LogInfo($"Loaded {_lootSpawns.Count} loot spawn locations");
	}

	private static double ClampPercent(double value) {
		if (value < 0) return 0;
		if (value > 100) return 100;
		return value;
	}

	private static readonly Dictionary<string, string> ArcTrackerMapIdAliases = new(StringComparer.OrdinalIgnoreCase) {
		["dam"] = "dam_battlegrounds"
	};

	private static string NormalizeMapId(string mapId) {
		string trimmed = mapId.Trim();
		if (ArcTrackerMapIdAliases.TryGetValue(trimmed, out var mapped)) return mapped;

		var maps = ArcRaidersData.GetMaps();
		if (maps.Count == 0) return trimmed;

		var exact = maps.FirstOrDefault(m => m.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
		if (exact != null) return exact.Id;

		string normalized = NormalizeToken(trimmed);
		if (string.IsNullOrWhiteSpace(normalized)) return trimmed;

		var match = maps
			.Select(m => new {
				Map = m,
				IdToken = NormalizeToken(m.Id),
				NameToken = NormalizeToken(m.GetName())
			})
			.OrderByDescending(m => m.IdToken == normalized)
			.ThenByDescending(m => m.NameToken == normalized)
			.ThenByDescending(m => m.IdToken.Contains(normalized))
			.ThenByDescending(m => m.NameToken.Contains(normalized))
			.ThenBy(m => m.IdToken.Length)
			.FirstOrDefault(m =>
				m.IdToken == normalized
				|| m.NameToken == normalized
				|| m.IdToken.Contains(normalized)
				|| m.NameToken.Contains(normalized));

		return match?.Map.Id ?? trimmed;
	}

	private static string NormalizeToken(string? value) {
		if (string.IsNullOrWhiteSpace(value)) return "";
		var filtered = new string(value
			.Where(c => char.IsLetterOrDigit(c))
			.ToArray());
		return filtered.ToLowerInvariant();
	}

	private static string BuildItemType(ArcTrackerPoint point) {
		if (!string.IsNullOrWhiteSpace(point.InstanceName)) return point.InstanceName.Trim();
		string category = Humanize(point.Category);
		string subcategory = Humanize(point.Subcategory);
		if (!string.IsNullOrWhiteSpace(subcategory)) return $"{category}: {subcategory}";
		return category;
	}

	private static string Humanize(string? value) {
		if (string.IsNullOrWhiteSpace(value)) return "Unknown";
		string cleaned = value.Replace("_", " ").Trim();
		return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cleaned);
	}

	private static string MapRarity(ArcTrackerPoint point) {
		string category = point.Category?.Trim().ToLowerInvariant() ?? "";
		string sub = point.Subcategory?.Trim().ToLowerInvariant() ?? "";

		if (category == "events") return "legendary";
		if (category == "arc") return "rare";
		if (category == "locations") return "common";

		if (category == "containers") {
			if (sub.Contains("raider_cache") || sub.Contains("weapon_case") || sub.Contains("arc_courier")) return "epic";
			if (sub.Contains("baron") || sub.Contains("breachable")) return "rare";
			if (sub.Contains("med") || sub.Contains("ammo")) return "uncommon";
			return "common";
		}

		return "common";
	}

	private static bool IsCategoryEnabled(string? category) {
		string normalized = category?.Trim().ToLowerInvariant() ?? "";
		return normalized switch {
			"containers" => RatConfig.Map.ShowContainers,
			"arc" => RatConfig.Map.ShowArc,
			"events" => RatConfig.Map.ShowEvents,
			"locations" => RatConfig.Map.ShowLocations,
			_ => true
		};
	}

	private sealed class ArcTrackerPoint {
		[JsonPropertyName("id")]
		public string? Id { get; set; }
		[JsonPropertyName("lat")]
		public double Lat { get; set; }
		[JsonPropertyName("lng")]
		public double Lng { get; set; }
		[JsonPropertyName("mapID")]
		public string? MapId { get; set; }
		[JsonPropertyName("category")]
		public string? Category { get; set; }
		[JsonPropertyName("subcategory")]
		public string? Subcategory { get; set; }
		[JsonPropertyName("instanceName")]
		public string? InstanceName { get; set; }
		[JsonPropertyName("behindLockedDoor")]
		public bool BehindLockedDoor { get; set; }
		[JsonPropertyName("eventConditionMask")]
		public int EventConditionMask { get; set; }
		[JsonPropertyName("lootAreas")]
		public string? LootAreas { get; set; }
	}
	
	public class MinimapData {
		public string MapId { get; set; } = "";
		public string MapName { get; set; } = "";
		public double PlayerX { get; set; }
		public double PlayerY { get; set; }
		public string? MapImageUrl { get; set; }
		public List<LootSpawn> NearbyLoot { get; set; } = new();
		public List<ActiveQuestInfo> ActiveQuests { get; set; } = new();
	}
	
	public class ActiveQuestInfo {
		public string QuestId { get; set; } = "";
		public string QuestName { get; set; } = "";
		public List<QuestObjective> Objectives { get; set; } = new();
	}
	
	public class QuestObjective {
		public string Description { get; set; } = "";
		public bool IsCompleted { get; set; } = false;
	}
	
	public class InteractiveMapData {
		public string MapId { get; set; } = "";
		public string MapName { get; set; } = "";
		public string? MapImageUrl { get; set; }
		public MapPosition? PlayerPosition { get; set; }
		public List<LootSpawn> LootSpawns { get; set; } = new();
		public List<QuestLocation> QuestLocations { get; set; } = new();
	}
	
	public class QuestLocation {
		public string QuestId { get; set; } = "";
		public string QuestName { get; set; } = "";
		public double X { get; set; }
		public double Y { get; set; }
		public string Objective { get; set; } = "";
	}
}
