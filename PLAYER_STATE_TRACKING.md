# Player State Tracking System

Complete guide to the quest tracking, workbench management, map overlays, and intelligent recommendation system.

## Overview

The Player State Tracking System provides comprehensive in-raid assistance by:
- Tracking active quests and their objectives
- Managing workbench levels and learned blueprints
- Displaying real-time minimap with loot locations
- Providing interactive full map with quest markers
- Enhancing item recommendations based on your progress

## Features

### 1. Quest Tracking

**QuestTracker Component**
- Displays active quests with objectives and rewards
- Browse all 100+ quests from RaidTheory data
- Filter quests by trader (Celeste, Lance, etc.)
- Mark quests as complete
- Items needed for active quests show ★ priority marker

**Usage:**
```csharp
// Add active quest
PlayerStateManager.AddActiveQuest("ss5"); // "A Bad Feeling"

// Complete quest
PlayerStateManager.CompleteQuest("ss5");

// Check quest status
bool isActive = PlayerStateManager.IsQuestActive("ss5");
bool isDone = PlayerStateManager.IsQuestCompleted("ss5");

// Get quests by trader
var celesteQuests = ArcRaidersData.GetQuestsByTrader("Celeste");
```

**UI Features:**
- Active quests displayed in collapsible cards
- Shows trader, objectives, and reward items
- "Browse Quests" button opens full quest database
- Trader filter in quest browser
- Visual status indicators (⚡ Active, ✓ Completed)

### 2. Workbench & Blueprint Management

**WorkbenchTracker Component**
- Track levels for all 9 hideout modules (Stash, Weapon Bench, etc.)
- View upgrade requirements (materials + coin costs)
- Manage learned blueprints/projects
- See what items you can actually craft

**Usage:**
```csharp
// Set workbench level
PlayerStateManager.SetWorkbenchLevel("weapon_bench", 3);
PlayerStateManager.SetWorkbenchLevel("stash", 5);

// Get current level
int level = PlayerStateManager.GetWorkbenchLevel("weapon_bench");

// Learn blueprint
PlayerStateManager.LearnBlueprint("expedition_project");

// Check if learned
bool knows = PlayerStateManager.IsBlueprintLearned("expedition_project");

// Get all projects
var projects = ArcRaidersData.GetProjects();
```

**UI Features:**
- Level up/down buttons (+ / -) for each workbench
- Shows next upgrade requirements
- Displays material costs and coin requirements
- Blueprint library with learned status
- Phase information for multi-phase projects

### 3. Map & Minimap System

**MinimapOverlay Component**
- Always-visible minimap (top-right corner)
- Real-time player position tracking
- Nearby loot indicators (color-coded by rarity)
- Click to open full interactive map

**MapOverlayManager**
- Position tracking and updating
- Loot spawn data management
- Quest location extraction

**Usage:**
```csharp
var mapManager = new MapOverlayManager();

// Update player position
mapManager.UpdatePosition(x: 45.2, y: 67.8, mapId: "dam_battlegrounds");

// Get current map info
var currentMap = mapManager.GetCurrentMap();

// Generate minimap data
var minimapData = mapManager.GenerateMinimapData();
// Returns: MapId, MapName, PlayerX, PlayerY, NearbyLoot[]

// Generate full map data
var fullMapData = mapManager.GenerateInteractiveMapData("dam_battlegrounds");
// Returns: MapId, MapName, MapImageUrl, PlayerPosition, LootSpawns[], QuestLocations[]
```

**Minimap Features:**
- 250x230px always-visible display
- Pulsing player marker (📍)
- Loot markers with rarity colors:
  - 🟠 Legendary (orange)
  - 🟣 Epic (purple)
  - 🔵 Rare (blue)
  - 🟢 Uncommon (green)
  - ⚪ Common (white)
- "Nearby Loot" list shows closest 5 items
- Expand button (🗺️) opens full map

**Full Map Features:**
- 90vw x 90vh modal display
- Zoom controls (0.5x - 3.0x)
- Toggle loot spawn markers (💎)
- Toggle quest markers (❗)
- Scrollable/pannable large map
- Legend with marker explanations

### 4. State Detection System

**StateDetectionManager**
- Periodic screen capture (every 5 seconds)
- Detects active menu (Quest/Workbench/Blueprint/Map/Tracked Resources)
- Extracts information via OCR (framework ready)
- Updates PlayerStateManager automatically

**Usage:**
```csharp
var stateDetection = new StateDetectionManager();

// Start automatic detection
stateDetection.Start();

// Listen for state changes
stateDetection.StateDetected += (state) => {
    switch (state) {
        case StateDetectionManager.DetectedState.QuestMenu:
            Console.WriteLine("Quest menu detected");
            break;
        case StateDetectionManager.DetectedState.WorkbenchMenu:
            Console.WriteLine("Workbench menu detected");
            break;
        // ... etc
    }
};

// Stop detection
stateDetection.Stop();
```

**Detection Framework:**
- `ContainsQuestMenuIndicators()` - Quest menu detection
- `ContainsWorkbenchMenuIndicators()` - Workbench detection
- `ContainsBlueprintMenuIndicators()` - Blueprint menu detection
- `ContainsTrackedResourcesIndicators()` - Tracked resources detection
- `ContainsMapViewIndicators()` - Map view detection

**OCR Integration Points:**
- `ExtractQuestInfo()` - Parse active quest names
- `ExtractWorkbenchInfo()` - Extract workbench name & level
- `ExtractBlueprintInfo()` - Detect learned blueprints
- `ExtractTrackedResourcesInfo()` - Parse tracked item list

### 5. Enhanced Item Recommendations

Items are now evaluated with player state priority:

**Priority Order:**
1. ★ **Manually Tracked** - Items you explicitly mark as tracked
2. ★ **Quest/Project Required** - Items needed for active quests or projects
3. **Quest Items** - General quest-related items
4. **Base Items** - Hideout construction materials
5. **Crafting Items** - High-utility crafting materials
6. **Value-based** - Standard recycle/sell logic

**Usage:**
```csharp
// Track a specific item
PlayerStateManager.AddTrackedResource("arc_alloy");
PlayerStateManager.AddTrackedResource("metal_parts");

// Check if item is needed
bool needed = PlayerStateManager.IsItemNeeded("arc_alloy");
// Returns true if: tracked, needed for quest, or needed for project

// Get recycle recommendation (now player-state aware)
var (decision, reason) = item.GetRecycleRecommendation();
// If tracked: (Keep, "★ Manually tracked")
// If quest needed: (Keep, "★ Needed for quest/project")
```

## Data Storage

### Player State File
Location: `{AppDirectory}/player_state.json`

Structure:
```json
{
  "activeQuests": ["ss5", "ss7", "building_a_library"],
  "completedQuests": ["ss1", "ss2", "ss3", "ss4"],
  "trackedResources": ["metal_parts", "arc_alloy", "duct_tape"],
  "workbenchLevels": {
    "weapon_bench": 3,
    "stash": 5,
    "med_station": 2
  },
  "learnedBlueprints": ["expedition_project", "trailblazer_blueprint"],
  "lastUpdated": "2026-01-22T00:15:30.123Z"
}
```

### Auto-Save
Player state is automatically saved after every change:
- Quest activation/completion
- Workbench level change
- Blueprint learn/unlearn
- Resource tracking changes

## Integration Examples

### Example 1: Quest-Aware Scanning

```csharp
// Player activates quest "A Bad Feeling" (requires searching ARC Probes)
PlayerStateManager.AddActiveQuest("ss5");

// Later, when scanning items in-raid
var item = ArcRaidersData.GetItemByName("ARC Probe Scanner");
var (decision, reason) = item.GetRecycleRecommendation();
// Result: (Keep, "★ Needed for quest/project")
```

### Example 2: Workbench-Specific Crafting

```csharp
// Player has Weapon Bench at level 3
PlayerStateManager.SetWorkbenchLevel("weapon_bench", 3);

// Player learns a high-level weapon blueprint
PlayerStateManager.LearnBlueprint("vulcano_blueprint");

// Now scans materials needed for that blueprint
var arcAlloy = ArcRaidersData.GetItemByName("Arc Alloy");
var (decision, reason) = arcAlloy.GetRecycleRecommendation();
// Result: (Keep, "★ Needed for quest/project")
```

### Example 3: Map-Based Looting

```csharp
var mapManager = new MapOverlayManager();

// Update position based on detection
mapManager.UpdatePosition(x: 45, y: 67, mapId: "dam_battlegrounds");

// Get nearby loot
var minimapData = mapManager.GenerateMinimapData();
foreach (var loot in minimapData.NearbyLoot) {
    Console.WriteLine($"Nearby: {loot.ItemType} ({loot.Rarity}) at ({loot.X}, {loot.Y})");
}

// Show full map with quest markers
var fullMap = mapManager.GenerateInteractiveMapData("dam_battlegrounds");
foreach (var quest in fullMap.QuestLocations) {
    Console.WriteLine($"Quest: {quest.QuestName} at ({quest.X}, {quest.Y})");
}
```

## UI Component Integration

### Adding to Overlay

```razor
@* In your main overlay page *@
@using RatScanner.Pages.Components

<MinimapOverlay IsVisible="@ShowMinimap" />
<QuestTracker />
<WorkbenchTracker />
```

### Toggle Visibility

```csharp
// Toggle minimap
bool ShowMinimap = true;

// Hotkey binding
if (Input.IsKeyPressed(Key.M)) {
    ShowMinimap = !ShowMinimap;
}
```

## Configuration

### State Detection Settings

```csharp
// Adjust capture interval (default: 5000ms)
private const int CaptureIntervalMs = 5000;

// Enable/disable automatic detection
stateDetection.Start(); // Enable
stateDetection.Stop();  // Disable
```

### Map Display Settings

```csharp
// Minimap size (in MinimapOverlay.razor)
width: 250px;
height: 230px;

// Nearby loot radius
var nearbyLoot = GetNearbyLoot(playerX, playerY, radius: 100);

// Full map modal size
width: 90vw;
height: 90vh;
```

## Future Enhancements

**OCR Integration:**
- Add Tesseract or similar OCR library
- Implement text extraction in StateDetectionManager
- Auto-populate quests/workbenches from screenshots

**Loot Spawn Data:**
- Import community loot spawn data
- Add dynamic spawn probabilities
- Show event-specific loot

**Position Detection:**
- Implement minimap corner detection
- Extract player position from game UI
- Real-time position tracking

**Quest Objective Parsing:**
- NLP-based objective location extraction
- Auto-add map markers for quest objectives
- Distance calculation to quest locations

## Troubleshooting

**Player state not saving:**
- Check write permissions for `player_state.json`
- Verify PlayerStateManager.SaveState() is called
- Check logs for serialization errors

**Minimap not showing:**
- Ensure map data is downloaded from RaidTheory
- Check MapOverlayManager.UpdatePosition() is called
- Verify position coordinates are valid (0-100%)

**State detection not working:**
- Start StateDetectionManager with .Start()
- Check screen capture permissions
- Verify OCR methods are implemented

**Quest/Blueprint not found:**
- Ensure RaidTheory data is downloaded
- Check quest/blueprint ID matches RaidTheory format
- Verify data load with ArcRaidersData.GetQuests()

## API Reference

### PlayerStateManager

```csharp
// Quest Management
void AddActiveQuest(string questId)
void RemoveActiveQuest(string questId)
void CompleteQuest(string questId)
bool IsQuestActive(string questId)
bool IsQuestCompleted(string questId)

// Resource Tracking
void AddTrackedResource(string itemId)
void RemoveTrackedResource(string itemId)
bool IsResourceTracked(string itemId)

// Workbench Management
void SetWorkbenchLevel(string workbenchId, int level)
int GetWorkbenchLevel(string workbenchId)

// Blueprint Management
void LearnBlueprint(string blueprintId)
void UnlearnBlueprint(string blueprintId)
bool IsBlueprintLearned(string blueprintId)

// Utility
bool IsItemNeeded(string itemId)
Dictionary<string, int> GetQuestRequiredItems()
Dictionary<string, int> GetProjectRequiredItems()
PlayerState GetState()
void ClearState()
```

### ArcRaidersData Extensions

```csharp
// Quests
List<RaidTheoryQuest> GetQuests()
RaidTheoryQuest? GetQuestById(string id)
List<RaidTheoryQuest> GetQuestsByTrader(string trader)

// Projects/Blueprints
List<RaidTheoryProject> GetProjects()
RaidTheoryProject? GetProjectById(string id)

// Maps
List<RaidTheoryMap> GetMaps()
RaidTheoryMap? GetMapById(string id)
```

### MapOverlayManager

```csharp
void UpdatePosition(double x, double y, string mapId)
MapPosition? GetCurrentPosition()
RaidTheoryMap? GetCurrentMap()
MinimapData? GenerateMinimapData()
InteractiveMapData GenerateInteractiveMapData(string mapId)
void LoadLootSpawnData()
```

## See Also

- [RAIDTHEORY_INTEGRATION.md](RAIDTHEORY_INTEGRATION.md) - Data source integration
- [ADDING_ITEMS.md](ADDING_ITEMS.md) - Item data management
- [README.md](README.md) - General Rat Scanner usage
