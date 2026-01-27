# Adding Arc Raiders Items

This document explains how the Arc Raiders item database works and how items are managed.

## Data Source

**Primary Source:** [RaidTheory/arcraiders-data](https://github.com/RaidTheory/arcraiders-data)

This comprehensive GitHub repository is automatically synced and provides:
- 500+ items with detailed information
- Trader exchange data
- Skill tree nodes
- Hideout module upgrade paths
- AI-upscaled item images

**Fallback Source:** Legacy JSON file (`Resources/ArcRaidersItems.json`)

The tool will automatically download and cache data from the RaidTheory repository on first launch. Data is refreshed automatically every 24 hours.

## How Items are Loaded

Items are now loaded from the RaidTheory repository which provides individual JSON files for each item in the `items/` directory. The data structure includes:

```json
{
  "id": "item_id",
  "name": {
    "en": "Item Name"
  },
  "type": "Category",
  "rarity": "Common",
  "value": 100,
  "weightKg": 0.5,
  "stackSize": 1,
  "recyclesInto": {
    "material_1": 2,
    "material_2": 1
  },
  "imageFilename": "https://cdn.arctracker.io/items/item_id.png"
}
```

## Manual Data Management

### Updating Item Data

To force a refresh of the RaidTheory data:
1. Call `ArcRaidersData.RefreshRaidTheoryDataAsync()` from code
2. Or delete the cache directory at: `%TEMP%/RatScanner/Cache/RaidTheoryData`
3. Restart the application

### Override Files

You can still use local override files in the `Resources/` directory:
- `ArcRaidersRecycleValues.json` - Override recycle values
- `ArcRaidersRecycleOutputs.json` - Override recycle outputs
- `ArcRaidersCraftingKeep.json` - Mark items for crafting
- `ArcRaidersCraftingUsage.json` - Define crafting usage counts
- `ArcRaidersCraftingWeights.json` - Adjust scoring weights

These override files take precedence over the RaidTheory data.

## Accessing Additional Data

The RaidTheory repository provides more than just items:

### Trades
```csharp
var trades = ArcRaidersData.GetTrades();
var celesteTrades = ArcRaidersData.GetTradesByTrader("Celeste");
```

### Skill Nodes
```csharp
var skillNodes = ArcRaidersData.GetSkillNodes();
var combatSkills = ArcRaidersData.GetSkillNodesByCategory("COMBAT");
```

### Hideout Modules
```csharp
var hideoutModules = ArcRaidersData.GetHideoutModules();
var stash = ArcRaidersData.GetHideoutModuleById("stash");
```

## Recycle Logic

Items are evaluated for recycling based on:

1. **Quest Items** (`IsQuestItem = true`) - Never recycle
2. **Base Items** (`IsBaseItem = true`) - Never recycle  
3. **Crafting Items** (`IsCraftingItem = true`) - Weighted based on usage
4. **Value per slot** - If less than 50 credits/slot → recycle
5. **Total value** - If more than 200 credits → keep
6. **Default** - Recycle

To adjust these thresholds, edit `RatScanner/ArcItemExtensions.cs` in the `GetRecycleRecommendation()` method.

Crafting weights can be configured in `Resources/ArcRaidersCraftingWeights.json`.

## Contributing to RaidTheory Data

If you find errors or want to add missing data, contribute directly to the upstream repository:

**Repository:** https://github.com/RaidTheory/arcraiders-data

1. Fork the repository
2. Make your changes
3. Format with `bun run format`
4. Submit a pull request

Your changes will be automatically picked up by Rat Scanner on the next data refresh.

## Testing

After making changes:
1. Delete the cache directory to force re-download: `%TEMP%/RatScanner/Cache/RaidTheoryData`
2. Build the project
3. Launch the scanner
4. Verify items load correctly
5. Check that recycle recommendations work as expected

## Data Structure Reference

### Item Properties (from RaidTheory)
- `id` - Unique item identifier (kebab-case)
- `name` - Localized names (multiple languages)
- `type` - Item category
- `rarity` - Common, Uncommon, Rare, Epic, Legendary
- `value` - Base credit value
- `weightKg` - Weight in kilograms
- `stackSize` - Maximum stack size
- `recyclesInto` - Dictionary of output items and quantities
- `salvagesInto` - Salvage outputs
- `imageFilename` - URL to item icon (arctracker.io CDN)
