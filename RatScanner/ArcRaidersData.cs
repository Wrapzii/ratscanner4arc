using System;
using System.Collections.Generic;
using System.Linq;

namespace RatScanner;

/// <summary>
/// Static hardcoded data for Arc Raiders items
/// Source: https://metaforge.app/arc-raiders/database/items/page/1
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
		public bool IsQuestItem { get; set; } = false;
		public bool IsBaseItem { get; set; } = false; // For base building
		public string? ImageLink { get; set; }
		public string? WikiLink { get; set; }
		public string Category { get; set; } = "General";
		
		// Calculated property
		public int ValuePerSlot => Value / Math.Max(1, Width * Height);
	}
	
	/// <summary>
	/// Hardcoded Arc Raiders items database
	/// TODO: Populate with actual data from https://metaforge.app/arc-raiders/database/items/page/1
	/// </summary>
	private static readonly List<ArcItem> Items = new() {
		// Example items - these should be replaced with real Arc Raiders data
		new ArcItem {
			Id = "arc_item_001",
			Name = "Scrap Metal",
			ShortName = "Scrap",
			Width = 1,
			Height = 1,
			Value = 50,
			IsQuestItem = false,
			IsBaseItem = true,
			Category = "Materials"
		},
		new ArcItem {
			Id = "arc_item_002",
			Name = "Advanced Circuit",
			ShortName = "Circuit",
			Width = 1,
			Height = 1,
			Value = 500,
			IsQuestItem = true,
			IsBaseItem = true,
			Category = "Electronics"
		},
		new ArcItem {
			Id = "arc_item_003",
			Name = "Rusty Can",
			ShortName = "Can",
			Width = 1,
			Height = 1,
			Value = 10,
			IsQuestItem = false,
			IsBaseItem = false,
			Category = "Junk"
		},
		new ArcItem {
			Id = "arc_item_004",
			Name = "Medical Supplies",
			ShortName = "Meds",
			Width = 2,
			Height = 1,
			Value = 300,
			IsQuestItem = true,
			IsBaseItem = false,
			Category = "Medical"
		},
		new ArcItem {
			Id = "arc_item_005",
			Name = "Weapon Parts",
			ShortName = "W.Parts",
			Width = 2,
			Height = 2,
			Value = 1000,
			IsQuestItem = false,
			IsBaseItem = true,
			Category = "Weapons"
		},
		// Add more items as needed from metaforge.app database
	};
	
	/// <summary>
	/// Get all Arc Raiders items
	/// </summary>
	public static ArcItem[] GetItems() => Items.ToArray();
	
	/// <summary>
	/// Get item by ID
	/// </summary>
	public static ArcItem? GetItemById(string id) => Items.FirstOrDefault(i => i.Id == id);
	
	/// <summary>
	/// Get item by name
	/// </summary>
	public static ArcItem? GetItemByName(string name) => Items.FirstOrDefault(i => 
		i.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || 
		i.ShortName.Equals(name, StringComparison.OrdinalIgnoreCase));
	
	/// <summary>
	/// Get items by category
	/// </summary>
	public static ArcItem[] GetItemsByCategory(string category) => 
		Items.Where(i => i.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
	
	/// <summary>
	/// Get all quest items
	/// </summary>
	public static ArcItem[] GetQuestItems() => Items.Where(i => i.IsQuestItem).ToArray();
	
	/// <summary>
	/// Get all base building items
	/// </summary>
	public static ArcItem[] GetBaseItems() => Items.Where(i => i.IsBaseItem).ToArray();
}
