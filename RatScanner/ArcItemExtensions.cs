using System;

namespace RatScanner;

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
	public static (bool shouldRecycle, string reason) GetRecycleRecommendation(this ArcRaidersData.ArcItem item) {
		// Check if item is needed for quests
		if (item.IsQuestItem) {
			return (false, "Needed for quest");
		}
		
		// Check if item is needed for base building
		if (item.IsBaseItem) {
			return (false, "Needed for base");
		}
		
		// Get value per slot to determine efficiency
		int valuePerSlot = item.ValuePerSlot;
		
		// Low value threshold - items worth less per slot should be recycled
		const int lowValueThreshold = 50;
		if (valuePerSlot < lowValueThreshold) {
			return (true, "Low value per slot");
		}
		
		// If item has decent value, keep it
		if (item.Value > 200) {
			return (false, "Good value");
		}
		
		// Default to recycle if no clear value
		return (true, "Low overall value");
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
