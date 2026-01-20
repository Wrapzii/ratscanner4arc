using System;

namespace RatScanner;

public enum RecycleDecision {
	Recycle,
	Sell,
	Keep
}

/// <summary>
/// Extension methods for Arc Raiders items
/// </summary>
public static class ArcItemExtensions {
	
	/// <summary>
	/// Get value per inventory slot for Arc Raiders item
	/// </summary>
	public static int GetValuePerSlot(this ArcRaidersData.ArcItem item) {
		return item.ValuePerSlot;
	}
	
	/// <summary>
	/// Determines if an Arc Raiders item should be recycled
	/// </summary>
	/// <param name="item">The item to evaluate</param>
	/// <returns>Tuple indicating if item should be recycled and the reason</returns>
	public static (RecycleDecision decision, string reason) GetRecycleRecommendation(this ArcRaidersData.ArcItem item) {
		// Check if item is needed for quests
		if (item.IsQuestItem) {
			return (RecycleDecision.Keep, "Needed for quest");
		}
		
		// Check if item is needed for base building
		if (item.IsBaseItem) {
			return (RecycleDecision.Keep, "Needed for base");
		}
		
		// Check if item is commonly needed for crafting
		if (item.IsCraftingItem) {
			string note = string.IsNullOrWhiteSpace(item.CraftingNote) ? "Crafting material" : item.CraftingNote;
			return (RecycleDecision.Keep, note);
		}
		
		// If recycle value is known, compare trader vs scrap
		if (item.RecycleValue > 0) {
			if (item.RecycleValue > item.Value) {
				return (RecycleDecision.Recycle, $"Recycle ({item.RecycleValue.AsCredits()} > {item.Value.AsCredits()})");
			}
			if (item.RecycleValue < item.Value) {
				return (RecycleDecision.Sell, $"Sell ({item.Value.AsCredits()} > {item.RecycleValue.AsCredits()})");
			}
			return (RecycleDecision.Sell, $"Either (both {item.Value.AsCredits()})");
		}
		
		bool isRecyclable = item.IsRecyclable || item.Category.Equals("Recyclable", StringComparison.OrdinalIgnoreCase);
		string heuristicSuffix = isRecyclable ? "" : " (heuristic)";
		
		// Fallback: use value per slot to determine efficiency
		int valuePerSlot = item.ValuePerSlot;
		const int lowValueThreshold = 50;
		if (valuePerSlot < lowValueThreshold) {
			return (RecycleDecision.Recycle, $"Recycle (low value per slot{heuristicSuffix})");
		}
		
		if (item.Value > 200) {
			return (RecycleDecision.Sell, $"Sell (good value{heuristicSuffix})");
		}
		
		return (RecycleDecision.Recycle, $"Recycle (low overall value{heuristicSuffix})");
	}
	
	/// <summary>
	/// Format value as credits for display
	/// </summary>
	public static string AsCredits(this int value) {
		return $"{value:n0} CR";
	}
	
	/// <summary>
	/// Format value as credits for display
	/// </summary>
	public static string AsCredits(this int? value) => AsCredits(value ?? 0);
}
