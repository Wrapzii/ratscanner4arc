# Adding Arc Raiders Items

This document explains how to add new items to the hardcoded Arc Raiders database.

## Data Source

All item data should be sourced from: https://metaforge.app/arc-raiders/database/items/page/1

## How to Add Items

Items are defined in `RatScanner/ArcRaidersData.cs` in the `Items` list.

### Item Structure

```csharp
new ArcItem {
    Id = "arc_item_XXX",           // Unique identifier
    Name = "Full Item Name",        // Display name
    ShortName = "Short",            // Abbreviated name
    Width = 1,                      // Inventory width in slots
    Height = 1,                     // Inventory height in slots
    Value = 100,                    // Static value in credits
    IsQuestItem = false,            // True if needed for quests
    IsBaseItem = false,             // True if needed for base building
    Category = "CategoryName",      // Item category
    ImageLink = null,               // Optional: URL to item icon
    WikiLink = null                 // Optional: URL to wiki page
}
```

### Example

```csharp
new ArcItem {
    Id = "arc_item_006",
    Name = "Advanced Polymer",
    ShortName = "Polymer",
    Width = 1,
    Height = 2,
    Value = 450,
    IsQuestItem = true,
    IsBaseItem = true,
    Category = "Materials"
}
```

## Recycle Logic

Items are evaluated for recycling based on:

1. **Quest Items** (`IsQuestItem = true`) - Never recycle
2. **Base Items** (`IsBaseItem = true`) - Never recycle  
3. **Value per slot** - If less than 50 credits/slot → recycle
4. **Total value** - If more than 200 credits → keep
5. **Default** - Recycle

To adjust these thresholds, edit `RatScanner/ArcItemExtensions.cs` in the `GetRecycleRecommendation()` method.

## Categories

Common categories include:
- Materials
- Electronics
- Medical
- Weapons
- Junk
- Food
- Tools
- Consumables

Feel free to add new categories as needed for Arc Raiders items.

## Testing

After adding items:
1. Build the project
2. Launch the scanner
3. Scan items to verify they appear correctly
4. Check that recycle recommendations work as expected

## Future Improvements

Consider:
- Extracting items to a JSON file for easier editing
- Creating a tool to import from MetaForge API
- Adding rarity/tier information
- Supporting item variants
