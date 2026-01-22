# RaidTheory Data Integration

This document describes the integration of the [RaidTheory/arcraiders-data](https://github.com/RaidTheory/arcraiders-data) repository as the primary data source for Rat Scanner.

## Overview

Rat Scanner now automatically downloads and uses comprehensive game data from the RaidTheory repository, which includes:

- **500+ Items** with detailed information (name, value, weight, recycle outputs, etc.)
- **Trade Data** from all traders (Celeste, etc.)
- **Skill Tree Nodes** with prerequisites and upgrade paths
- **Hideout Modules** with upgrade requirements and costs
- **AI-Upscaled Images** from arctracker.io CDN

## Data Sources

### Primary: RaidTheory Repository
- **URL**: https://github.com/RaidTheory/arcraiders-data
- **Auto-download**: Yes, on first launch
- **Auto-refresh**: Every 24 hours
- **Cache location**: `%TEMP%/RatScanner/Cache/RaidTheoryData`

### Fallback: Legacy JSON
- **Location**: `Resources/ArcRaidersItems.json`
- **Used when**: RaidTheory data unavailable

### Override Files
- **Location**: `Resources/` directory
- **Files**:
  - `ArcRaidersRecycleValues.json` - Override recycle values
  - `ArcRaidersRecycleOutputs.json` - Override recycle outputs
  - `ArcRaidersCraftingKeep.json` - Mark items for crafting
  - `ArcRaidersCraftingUsage.json` - Define crafting usage counts
  - `ArcRaidersCraftingWeights.json` - Adjust scoring weights

## How It Works

### Startup Sequence

1. **Check for cached data**: Looks in `%TEMP%/RatScanner/Cache/RaidTheoryData`
2. **Load from cache**: If data exists and is less than 24 hours old
3. **Fallback to legacy**: If no cache, tries legacy JSON file
4. **Download RaidTheory**: If no data available, downloads from GitHub
5. **Apply overrides**: Applies any local override files
6. **Background refresh**: Queues a background refresh if data is old

### Data Loading Flow

```
┌─────────────────────────────────────────────┐
│  RaidTheory Data Available?                 │
│  (%TEMP%/RatScanner/Cache/RaidTheoryData)   │
└─────────────┬───────────────────────────────┘
              │
         Yes ─┼─ No
              │
              ↓
    ┌─────────────────┐           ┌──────────────────┐
    │ Load RaidTheory │           │ Legacy JSON File │
    │   (500+ items)  │           │    Available?    │
    └────────┬────────┘           └────────┬─────────┘
             │                              │
             │                         Yes ─┼─ No
             │                              │
             ↓                              ↓
    ┌─────────────────┐           ┌──────────────────┐
    │ Apply Overrides │           │ Download from    │
    │   & Success     │←──────────│    GitHub        │
    └─────────────────┘           └──────────────────┘
```

## API Usage

### Items

```csharp
// Get all items
var items = ArcRaidersData.GetItems();

// Get specific item
var guitar = ArcRaidersData.GetItemById("acoustic_guitar");

// Search by name
var item = ArcRaidersData.GetItemByName("Acoustic Guitar");

// Filter by category
var weapons = ArcRaidersData.GetItemsByCategory("Weapon");

// Quest and base items
var questItems = ArcRaidersData.GetQuestItems();
var baseItems = ArcRaidersData.GetBaseItems();
```

### Trades

```csharp
// Get all trades
var allTrades = ArcRaidersData.GetTrades();

// Filter by trader
var celesteTrades = ArcRaidersData.GetTradesByTrader("Celeste");

// Example trade structure
foreach (var trade in celesteTrades) {
    Console.WriteLine($"{trade.Trader}: {trade.Quantity}x {trade.ItemId}");
    Console.WriteLine($"  Cost: {trade.Cost?.Quantity}x {trade.Cost?.ItemId}");
    if (trade.DailyLimit.HasValue) {
        Console.WriteLine($"  Daily Limit: {trade.DailyLimit.Value}");
    }
}
```

### Skill Nodes

```csharp
// Get all skill nodes
var skillNodes = ArcRaidersData.GetSkillNodes();

// Filter by category
var combatSkills = ArcRaidersData.GetSkillNodesByCategory("COMBAT");
var conditioningSkills = ArcRaidersData.GetSkillNodesByCategory("CONDITIONING");

// Example skill node usage
foreach (var node in combatSkills) {
    var name = node.GetName(); // English name
    var desc = node.GetDescription(); // English description
    Console.WriteLine($"{name} ({node.Category})");
    Console.WriteLine($"  Max Points: {node.MaxPoints}");
    Console.WriteLine($"  Major: {node.IsMajor}");
    
    if (node.PrerequisiteNodeIds != null) {
        Console.WriteLine($"  Prerequisites: {string.Join(", ", node.PrerequisiteNodeIds)}");
    }
}
```

### Hideout Modules

```csharp
// Get all hideout modules
var hideoutModules = ArcRaidersData.GetHideoutModules();

// Get specific module
var stash = ArcRaidersData.GetHideoutModuleById("stash");

// Example hideout module usage
if (stash != null) {
    Console.WriteLine($"{stash.GetName()} - Max Level: {stash.MaxLevel}");
    
    foreach (var level in stash.Levels ?? Enumerable.Empty<RaidTheoryDataSource.RaidTheoryHideoutModule.HideoutLevel>()) {
        Console.WriteLine($"Level {level.Level}: {level.Description}");
        
        if (level.OtherRequirements != null) {
            Console.WriteLine($"  Requirements: {string.Join(", ", level.OtherRequirements)}");
        }
        
        if (level.RequirementItemIds != null && level.RequirementItemIds.Any()) {
            Console.WriteLine($"  Items needed: {string.Join(", ", level.RequirementItemIds)}");
        }
    }
}
```

### Force Refresh

```csharp
// Force download latest data from GitHub
await ArcRaidersData.RefreshRaidTheoryDataAsync();
// Note: Requires application restart to fully reload data
```

## Data Structure Examples

### Item (RaidTheory Format)

```json
{
  "id": "acoustic_guitar",
  "name": {
    "en": "Acoustic Guitar"
  },
  "type": "Quick Use",
  "rarity": "Legendary",
  "value": 7000,
  "weightKg": 1,
  "stackSize": 1,
  "recyclesInto": {
    "metal_parts": 4,
    "wires": 6
  },
  "imageFilename": "https://cdn.arctracker.io/items/acoustic_guitar.png"
}
```

### Trade

```json
{
  "trader": "Celeste",
  "itemId": "chemicals",
  "quantity": 1,
  "cost": {
    "itemId": "assorted_seeds",
    "quantity": 1
  },
  "dailyLimit": null
}
```

### Skill Node

```json
{
  "id": "cond_1",
  "name": {
    "en": "Used To The Weight"
  },
  "category": "CONDITIONING",
  "isMajor": true,
  "maxPoints": 5,
  "position": {
    "x": 25,
    "y": 75
  },
  "prerequisiteNodeIds": []
}
```

### Hideout Module

```json
{
  "id": "stash",
  "name": {
    "en": "Stash"
  },
  "maxLevel": 10,
  "levels": [
    {
      "level": 1,
      "description": "64 slots",
      "requirementItemIds": []
    },
    {
      "level": 2,
      "description": "+24 slots (88 total)",
      "otherRequirements": ["5000 Coins"]
    }
  ]
}
```

## Benefits

### For Users
- **Always Up-to-Date**: Automatic updates from RaidTheory repository
- **Comprehensive Data**: 500+ items, trades, skills, hideout modules
- **High Quality Images**: AI-upscaled item icons from arctracker.io
- **Offline Support**: Cached data works without internet

### For Developers
- **Easy Integration**: Simple API for accessing game data
- **Type-Safe**: Strongly-typed C# models
- **Flexible**: Override system for customization
- **Maintainable**: No manual data entry required

### For Contributors
- **Community-Driven**: Contribute to RaidTheory repository
- **Format Files**: Automatic formatting with `bun run format`
- **Immediate Impact**: Changes picked up on next refresh

## Troubleshooting

### Data not loading
1. Check internet connection
2. Delete cache: `%TEMP%/RatScanner/Cache/RaidTheoryData`
3. Restart application
4. Check logs for error messages

### Outdated data
1. Delete cache to force refresh
2. Or wait 24 hours for auto-refresh
3. Or call `RefreshRaidTheoryDataAsync()`

### Custom items not showing
1. Check override files in `Resources/` directory
2. Ensure JSON syntax is valid
3. Check file names match expected pattern
4. Restart application after changes

## Migration from MetaForge

The integration automatically handles migration:

1. **First Launch**: Downloads RaidTheory data
2. **Legacy Support**: Falls back to old JSON if needed
3. **Override Compatibility**: Existing override files still work
4. **No Breaking Changes**: Item lookup API remains the same

## Future Enhancements

Potential future improvements:

- [ ] UI for viewing trades in-app
- [ ] Skill tree visualization
- [ ] Hideout upgrade planner
- [ ] Item search with trade info
- [ ] Optimal skill path calculator
- [ ] Trader profit calculator

## Credits

- **Data Source**: [RaidTheory/arcraiders-data](https://github.com/RaidTheory/arcraiders-data)
- **Images**: [arctracker.io](https://arctracker.io)
- **Game**: Arc Raiders by Embark Studios AB

## See Also

- [ADDING_ITEMS.md](ADDING_ITEMS.md) - How to work with item data
- [RaidTheory Repository](https://github.com/RaidTheory/arcraiders-data)
- [arctracker.io](https://arctracker.io)
