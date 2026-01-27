using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RatScanner;

/// <summary>
/// Manages player state tracking including quests, resources, workbenches, and blueprints
/// </summary>
public static class PlayerStateManager {
	private static readonly object StateLock = new();
	private static PlayerState? CurrentState;
	private static string StatePath => Path.Combine(RatConfig.Paths.Base, "player_state.json");
	
	/// <summary>
	/// Player state data
	/// </summary>
	public class PlayerState {
		/// <summary>
		/// Active quest IDs
		/// </summary>
		public HashSet<string> ActiveQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Completed quest IDs
		/// </summary>
		public HashSet<string> CompletedQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Tracked resource item IDs (items player is tracking for quests/projects)
		/// </summary>
		public HashSet<string> TrackedResources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Workbench levels (workbench ID -> current level)
		/// </summary>
		public Dictionary<string, int> WorkbenchLevels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Learned blueprint/project IDs
		/// </summary>
		public HashSet<string> LearnedBlueprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Skill tree branch points (branch name -> total points)
		/// Branches: Conditioning, Mobility, Survival
		/// </summary>
		public Dictionary<string, int> SkillTreeBranches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// Available skill points to allocate
		/// </summary>
		public int AvailableSkillPoints { get; set; } = 0;
		
		/// <summary>
		/// Currently queued map ID (when in matchmaking)
		/// </summary>
		public string? QueuedMapId { get; set; }
		
		/// <summary>
		/// Currently queued map name (when in matchmaking)
		/// </summary>
		public string? QueuedMapName { get; set; }
		
		/// <summary>
		/// Last updated timestamp
		/// </summary>
		public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
		
		/// <summary>
		/// Last detected UI state name
		/// </summary>
		public string? LastDetectedState { get; set; }
		
		/// <summary>
		/// Whether we are currently considered in-raid
		/// </summary>
		public bool IsInRaid { get; set; }

		/// <summary>
		/// In-raid HUD: weapon label (best-effort OCR)
		/// </summary>
		public string? InRaidWeaponLabel { get; set; }

		/// <summary>
		/// In-raid HUD: ammo in magazine (if detected)
		/// </summary>
		public int? InRaidAmmoInMag { get; set; }

		/// <summary>
		/// In-raid HUD: ammo reserve (if detected)
		/// </summary>
		public int? InRaidAmmoReserve { get; set; }

		/// <summary>
		/// Last time in-raid HUD info was updated
		/// </summary>
		public DateTime? InRaidHudUpdatedUtc { get; set; }
		
		/// <summary>
		/// Craftable items by station/module ID
		/// </summary>
		public Dictionary<string, List<string>> CraftableItemsByStation { get; set; } = new(StringComparer.OrdinalIgnoreCase);
		
		/// <summary>
		/// All craftable item IDs detected so far
		/// </summary>
		public HashSet<string> CraftableItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}
	
	/// <summary>
	/// Load player state from disk
	/// </summary>
	public static PlayerState LoadState() {
		lock (StateLock) {
			if (CurrentState != null) return CurrentState;
			
			try {
				if (File.Exists(StatePath)) {
					string json = File.ReadAllText(StatePath);
					CurrentState = JsonSerializer.Deserialize<PlayerState>(json, new JsonSerializerOptions {
						PropertyNameCaseInsensitive = true
					});
					if (CurrentState != null) {
						Logger.LogInfo($"Loaded player state: {CurrentState.ActiveQuests.Count} active quests, {CurrentState.TrackedResources.Count} tracked resources");
						return CurrentState;
					}
				}
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to load player state: {ex.Message}");
			}
			
			CurrentState = new PlayerState();
			return CurrentState;
		}
	}
	
	/// <summary>
	/// Save player state to disk
	/// </summary>
	public static void SaveState() {
		lock (StateLock) {
			if (CurrentState == null) return;
			
			try {
				CurrentState.LastUpdated = DateTime.UtcNow;
				string json = JsonSerializer.Serialize(CurrentState, new JsonSerializerOptions {
					WriteIndented = true
				});
				File.WriteAllText(StatePath, json);
				Logger.LogDebug("Player state saved successfully");
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to save player state: {ex.Message}");
			}
		}
	}
	
	/// <summary>
	/// Get current player state
	/// </summary>
	public static PlayerState GetState() {
		return LoadState();
	}

	/// <summary>
	/// Update the current UI state and raid status
	/// </summary>
	public static void SetUiState(StateDetectionManager.DetectedState state, bool isInRaid) {
		var playerState = LoadState();
		lock (StateLock) {
			string stateName = state.ToString();
			if (playerState.LastDetectedState != stateName || playerState.IsInRaid != isInRaid) {
				playerState.LastDetectedState = stateName;
				playerState.IsInRaid = isInRaid;
				SaveState();
			}
		}
	}
	
	// Quest management
	
	public static void AddActiveQuest(string questId) {
		var state = LoadState();
		lock (StateLock) {
			state.ActiveQuests.Add(questId);
			SaveState();
		}
	}

	public static void SetActiveQuests(IEnumerable<string> questIds, bool replace = true) {
		var state = LoadState();
		lock (StateLock) {
			if (replace) {
				state.ActiveQuests.Clear();
			}
			foreach (var questId in questIds) {
				state.ActiveQuests.Add(questId);
			}
			SaveState();
		}
	}
	
	public static void RemoveActiveQuest(string questId) {
		var state = LoadState();
		lock (StateLock) {
			state.ActiveQuests.Remove(questId);
			SaveState();
		}
	}
	
	public static void CompleteQuest(string questId) {
		var state = LoadState();
		lock (StateLock) {
			state.ActiveQuests.Remove(questId);
			state.CompletedQuests.Add(questId);
			SaveState();
		}
	}
	
	public static bool IsQuestActive(string questId) {
		var state = LoadState();
		return state.ActiveQuests.Contains(questId);
	}
	
	public static bool IsQuestCompleted(string questId) {
		var state = LoadState();
		return state.CompletedQuests.Contains(questId);
	}
	
	// Resource tracking
	
	public static void AddTrackedResource(string itemId) {
		var state = LoadState();
		lock (StateLock) {
			state.TrackedResources.Add(itemId);
			SaveState();
		}
	}
	
	public static void RemoveTrackedResource(string itemId) {
		var state = LoadState();
		lock (StateLock) {
			state.TrackedResources.Remove(itemId);
			SaveState();
		}
	}
	
	public static bool IsResourceTracked(string itemId) {
		var state = LoadState();
		return state.TrackedResources.Contains(itemId);
	}

	public static void SetInRaidHud(string? weaponLabel, int? ammoInMag, int? ammoReserve) {
		var state = LoadState();
		lock (StateLock) {
			bool changed = !string.Equals(state.InRaidWeaponLabel, weaponLabel, StringComparison.OrdinalIgnoreCase)
				|| state.InRaidAmmoInMag != ammoInMag
				|| state.InRaidAmmoReserve != ammoReserve;
			if (!changed) return;
			state.InRaidWeaponLabel = weaponLabel;
			state.InRaidAmmoInMag = ammoInMag;
			state.InRaidAmmoReserve = ammoReserve;
			state.InRaidHudUpdatedUtc = DateTime.UtcNow;
			SaveState();
		}
	}
	
	// Workbench management
	
	public static void SetWorkbenchLevel(string workbenchId, int level) {
		var state = LoadState();
		lock (StateLock) {
			state.WorkbenchLevels[workbenchId] = level;
			SaveState();
		}
	}

	public static void SetWorkbenchLevels(Dictionary<string, int> levels, bool replace = true) {
		var state = LoadState();
		lock (StateLock) {
			if (replace) {
				state.WorkbenchLevels.Clear();
			}
			foreach (var kvp in levels) {
				state.WorkbenchLevels[kvp.Key] = kvp.Value;
			}
			SaveState();
		}
	}
	
	/// <summary>
	/// Set craftable items detected for a station/module
	/// </summary>
	public static void SetCraftableItems(string? stationId, IEnumerable<string> itemIds) {
		var state = LoadState();
		if (string.IsNullOrWhiteSpace(stationId)) {
			stationId = "unknown";
		}
		var newSet = new HashSet<string>(itemIds, StringComparer.OrdinalIgnoreCase);
		lock (StateLock) {
			state.CraftableItemsByStation.TryGetValue(stationId, out var existingList);
			var existingSet = existingList == null
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				: new HashSet<string>(existingList, StringComparer.OrdinalIgnoreCase);
			
			if (!newSet.SetEquals(existingSet)) {
				state.CraftableItemsByStation[stationId] = newSet.ToList();
				foreach (var itemId in newSet) {
					state.CraftableItems.Add(itemId);
				}
				SaveState();
				Logger.LogInfo($"Craftables updated for {stationId}: {newSet.Count} items");
			}
		}
	}

	/// <summary>
	/// Get all craftable item IDs
	/// </summary>
	public static HashSet<string> GetCraftableItems() {
		var state = LoadState();
		return new HashSet<string>(state.CraftableItems, StringComparer.OrdinalIgnoreCase);
	}
	
	public static int GetWorkbenchLevel(string workbenchId) {
		var state = LoadState();
		return state.WorkbenchLevels.GetValueOrDefault(workbenchId, 0);
	}
	
	// Blueprint management
	
	public static void LearnBlueprint(string blueprintId) {
		var state = LoadState();
		lock (StateLock) {
			state.LearnedBlueprints.Add(blueprintId);
			SaveState();
		}
	}
	
	public static void UnlearnBlueprint(string blueprintId) {
		var state = LoadState();
		lock (StateLock) {
			state.LearnedBlueprints.Remove(blueprintId);
			SaveState();
		}
	}
	
	public static bool IsBlueprintLearned(string blueprintId) {
		var state = LoadState();
		return state.LearnedBlueprints.Contains(blueprintId);
	}
	
	/// <summary>
	/// Get all items needed for active quests
	/// </summary>
	public static Dictionary<string, int> GetQuestRequiredItems() {
		var state = LoadState();
		var requiredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		
		foreach (var questId in state.ActiveQuests) {
			var quest = ArcRaidersData.GetQuestById(questId);
			if (quest?.RewardItemIds == null) continue;
			
			// Note: Quest objectives might contain item requirements, but the structure
			// doesn't explicitly list them. This would need OCR/parsing of objective text
		}
		
		return requiredItems;
	}
	
	/// <summary>
	/// Get all items needed for tracked projects/blueprints
	/// </summary>
	public static Dictionary<string, int> GetProjectRequiredItems() {
		var requiredItems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		
		var projects = ArcRaidersData.GetProjects();
		foreach (var project in projects.Where(p => !p.Disabled)) {
			if (project.Phases == null) continue;
			
			foreach (var phase in project.Phases) {
				if (phase.RequirementItemIds == null) continue;
				
				foreach (var req in phase.RequirementItemIds) {
					if (requiredItems.ContainsKey(req.ItemId)) {
						requiredItems[req.ItemId] += req.Quantity;
					} else {
						requiredItems[req.ItemId] = req.Quantity;
					}
				}
			}
		}
		
		return requiredItems;
	}
	
	/// <summary>
	/// Check if an item is needed based on player state
	/// </summary>
	public static bool IsItemNeeded(string itemId) {
		var state = LoadState();
		
		// Check if manually tracked
		if (state.TrackedResources.Contains(itemId)) return true;
		
		// Check if needed for active quests
		var questItems = GetQuestRequiredItems();
		if (questItems.ContainsKey(itemId)) return true;
		
		// Check if needed for projects (if player has corresponding blueprint)
		var projectItems = GetProjectRequiredItems();
		if (projectItems.ContainsKey(itemId)) return true;
		
		return false;
	}
	
	// Skill Tree management
	
	/// <summary>
	/// Set skill tree branch points and available points
	/// </summary>
	public static void SetSkillTreeData(Dictionary<string, int> branchPoints, int availablePoints) {
		var state = LoadState();
		lock (StateLock) {
			bool changed = false;
			
			// Update branch points
			foreach (var kvp in branchPoints) {
				if (!state.SkillTreeBranches.TryGetValue(kvp.Key, out int existing) || existing != kvp.Value) {
					state.SkillTreeBranches[kvp.Key] = kvp.Value;
					changed = true;
				}
			}
			
			// Update available points
			if (state.AvailableSkillPoints != availablePoints) {
				state.AvailableSkillPoints = availablePoints;
				changed = true;
			}
			
			if (changed) {
				SaveState();
				Logger.LogDebug($"Skill tree data updated: {branchPoints.Count} branches, {availablePoints} available");
			}
		}
	}
	
	/// <summary>
	/// Get skill points for a specific branch
	/// </summary>
	public static int GetBranchPoints(string branchName) {
		var state = LoadState();
		return state.SkillTreeBranches.GetValueOrDefault(branchName, 0);
	}
	
	/// <summary>
	/// Get all skill tree branch data
	/// </summary>
	public static Dictionary<string, int> GetAllBranchPoints() {
		var state = LoadState();
		return new Dictionary<string, int>(state.SkillTreeBranches, StringComparer.OrdinalIgnoreCase);
	}
	
	/// <summary>
	/// Get available skill points
	/// </summary>
	public static int GetAvailableSkillPoints() {
		var state = LoadState();
		return state.AvailableSkillPoints;
	}
	
	/// <summary>
	/// Get total skill points (sum of all branches)
	/// </summary>
	public static int GetTotalSkillPoints() {
		var state = LoadState();
		return state.SkillTreeBranches.Values.Sum();
	}
	
	// Queue/Matchmaking management
	
	/// <summary>
	/// Set the queued map when entering matchmaking
	/// </summary>
	public static void SetQueuedMap(string mapId, string mapName) {
		var state = LoadState();
		lock (StateLock) {
			if (state.QueuedMapId != mapId || state.QueuedMapName != mapName) {
				state.QueuedMapId = mapId;
				state.QueuedMapName = mapName;
				SaveState();
				Logger.LogDebug($"Queued map set: {mapName} ({mapId})");
			}
		}
	}
	
	/// <summary>
	/// Get the currently queued map ID
	/// </summary>
	public static string? GetQueuedMapId() {
		var state = LoadState();
		return state.QueuedMapId;
	}
	
	/// <summary>
	/// Get the currently queued map name
	/// </summary>
	public static string? GetQueuedMapName() {
		var state = LoadState();
		return state.QueuedMapName;
	}
	
	/// <summary>
	/// Clear queued map (when leaving queue or entering raid)
	/// </summary>
	public static void ClearQueuedMap() {
		var state = LoadState();
		lock (StateLock) {
			if (state.QueuedMapId != null || state.QueuedMapName != null) {
				state.QueuedMapId = null;
				state.QueuedMapName = null;
				SaveState();
				Logger.LogDebug("Queued map cleared");
			}
		}
	}
	
	/// <summary>
	/// Clear all player state (for testing or reset)
	/// </summary>
	public static void ClearState() {
		lock (StateLock) {
			CurrentState = new PlayerState();
			SaveState();
			Logger.LogInfo("Player state cleared");
		}
	}
}
