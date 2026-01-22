using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RatScanner;

/// <summary>
/// Map overlay system for displaying minimap and interactive map
/// </summary>
public class MapOverlayManager {
	public class MapPosition {
		public double X { get; set; }
		public double Y { get; set; }
		public string MapId { get; set; } = "";
	}
	
	public class LootSpawn {
		public double X { get; set; }
		public double Y { get; set; }
		public string ItemType { get; set; } = "";
		public string Rarity { get; set; } = "";
	}
	
	private MapPosition? _currentPosition;
	private string? _currentMapId;
	private readonly List<LootSpawn> _lootSpawns = new();
	
	/// <summary>
	/// Get current player position (if detected)
	/// </summary>
	public MapPosition? GetCurrentPosition() => _currentPosition;
	
	/// <summary>
	/// Update player position based on detection
	/// </summary>
	public void UpdatePosition(double x, double y, string mapId) {
		_currentPosition = new MapPosition { X = x, Y = y, MapId = mapId };
		_currentMapId = mapId;
		Logger.LogDebug($"Position updated: ({x}, {y}) on {mapId}");
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
		if (map == null) return null;
		
		return new MinimapData {
			MapId = _currentMapId,
			MapName = map.GetName(),
			PlayerX = _currentPosition.X,
			PlayerY = _currentPosition.Y,
			MapImageUrl = map.Image,
			NearbyLoot = GetNearbyLoot(_currentPosition.X, _currentPosition.Y, 100) // 100 unit radius
		};
	}
	
	/// <summary>
	/// Generate full interactive map data
	/// </summary>
	public InteractiveMapData GenerateInteractiveMapData(string mapId) {
		var map = ArcRaidersData.GetMapById(mapId);
		if (map == null) {
			return new InteractiveMapData { MapId = mapId, MapName = mapId };
		}
		
		return new InteractiveMapData {
			MapId = mapId,
			MapName = map.GetName(),
			MapImageUrl = map.Image,
			PlayerPosition = _currentMapId == mapId ? _currentPosition : null,
			LootSpawns = _lootSpawns.Where(l => true).ToList(), // All loot for this map
			QuestLocations = GetQuestLocationsForMap(mapId)
		};
	}
	
	private List<LootSpawn> GetNearbyLoot(double x, double y, double radius) {
		// Calculate distance and return nearby loot
		return _lootSpawns.Where(loot => {
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
		
		// TODO: Load actual loot spawn data from RaidTheory or custom JSON
		// This would include item spawn locations, loot container positions, etc.
		
		Logger.LogInfo($"Loaded {_lootSpawns.Count} loot spawn locations");
	}
	
	public class MinimapData {
		public string MapId { get; set; } = "";
		public string MapName { get; set; } = "";
		public double PlayerX { get; set; }
		public double PlayerY { get; set; }
		public string? MapImageUrl { get; set; }
		public List<LootSpawn> NearbyLoot { get; set; } = new();
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
