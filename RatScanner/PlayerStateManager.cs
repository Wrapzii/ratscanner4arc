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
		/// Last updated timestamp
		/// </summary>
		public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
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
