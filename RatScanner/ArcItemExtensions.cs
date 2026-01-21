using System;
using System.Collections.Generic;
using System.Linq;

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
		var weights = ArcRaidersData.GetCraftingWeights();
		double itemScore = GetItemUtilityScore(item, weights);
		if (itemScore >= weights.KeepScoreThreshold) {
			return (RecycleDecision.Keep, $"High craft utility (score {itemScore:0.##})");
		}
		if (!item.HasCraftingUsageData) {
			ArcRaidersData.EnsureCraftingUsageFor(item.Id);
		}

		// Prefer recycling if it yields high-utility crafting materials
		if (TryGetRecycleCraftingOutputs(item, weights, out string recycleCraftingReason)) {
			return (RecycleDecision.Recycle, recycleCraftingReason);
		}
		
		// If recycle value is known, compare trader vs scrap
		if (item.RecycleValue > 0) {
			if (item.RecycleValue > item.Value) {
				return (RecycleDecision.Recycle, AppendLoadingNote(item, $"Recycle ({item.RecycleValue.AsCredits()} > {item.Value.AsCredits()})"));
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
		const int lowValueThreshold = 100;
		if (valuePerSlot < lowValueThreshold) {
			return (RecycleDecision.Recycle, AppendLoadingNote(item, $"Recycle (low value per slot{heuristicSuffix})"));
		}
		
		if (item.Value > 500) {
			return (RecycleDecision.Sell, $"Sell (good value{heuristicSuffix})");
		}
		
		return (RecycleDecision.Recycle, AppendLoadingNote(item, $"Recycle (low overall value{heuristicSuffix})"));
	}

	private static string AppendLoadingNote(ArcRaidersData.ArcItem item, string reason) {
		bool isRecyclable = item.IsRecyclable || item.Category.Equals("Recyclable", StringComparison.OrdinalIgnoreCase);
		if (!isRecyclable) return reason;
		if (item.RecycleOutputs != null && item.RecycleOutputs.Count > 0) return reason;
		return $"{reason} (outputs/loading...)";
	}

	private static bool TryGetRecycleCraftingOutputs(ArcRaidersData.ArcItem item, ArcRaidersData.CraftingWeightsConfig weights, out string reason) {
		reason = string.Empty;
		bool isRecyclable = item.IsRecyclable || item.Category.Equals("Recyclable", StringComparison.OrdinalIgnoreCase);
		if (!isRecyclable) return false;
		if (item.RecycleOutputs == null || item.RecycleOutputs.Count == 0) {
			ArcRaidersData.EnsureRecycleOutputsFor(item.Id);
			return false;
		}

		var matches = new List<(ArcRaidersData.RecycleOutput output, ArcRaidersData.ArcItem material)>();
		double outputScore = 0;
		foreach (var output in item.RecycleOutputs) {
			if (string.IsNullOrWhiteSpace(output.Id)) continue;
			var material = ArcRaidersData.GetItemById(output.Id);
			if (material == null) continue;
			double materialScore = GetItemUtilityScore(material, weights);
			outputScore += materialScore * Math.Max(1, output.Quantity);
			if (material.IsCraftingItem || material.IsBaseItem || materialScore >= weights.KeepScoreThreshold) {
				matches.Add((output, material));
			}
		}

		if (matches.Count == 0 || outputScore < weights.RecycleOutputScoreThreshold) return false;

		string FormatOutput((ArcRaidersData.RecycleOutput output, ArcRaidersData.ArcItem material) entry) {
			string name = string.IsNullOrWhiteSpace(entry.output.Name) ? entry.material.Name : entry.output.Name!;
			string quantity = entry.output.Quantity > 1 ? $"{entry.output.Quantity}x " : string.Empty;
			string note = string.IsNullOrWhiteSpace(entry.material.CraftingNote)
				? string.Empty
				: $" ({entry.material.CraftingNote})";
			return $"{quantity}{name}{note}";
		}

		const int maxOutputs = 5;
		var preview = matches.Take(maxOutputs).Select(FormatOutput).ToArray();
		int remaining = Math.Max(0, matches.Count - preview.Length);
		var lines = new List<string> { $"Recycle for high-utility outputs (score {outputScore:0.##}):" };
		for (int i = 0; i < preview.Length; i++) {
			bool isLast = i == preview.Length - 1 && remaining == 0;
			string prefix = isLast ? "└─" : "├─";
			lines.Add($"{prefix} {preview[i]}");
		}
		if (remaining > 0) {
			lines.Add($"└─ +{remaining} more");
		}

		if (item.RecycleValue > 0 && item.RecycleValue < item.Value) {
			lines[0] += $" (sell {item.Value.AsCredits()}, recycle {item.RecycleValue.AsCredits()})";
		}

		reason = string.Join(Environment.NewLine, lines);
		return true;
	}

	private static double GetItemUtilityScore(ArcRaidersData.ArcItem item, ArcRaidersData.CraftingWeightsConfig weights) {
		double rarityMultiplier = GetRarityMultiplier(item.Rarity);
		int usedInCount = item.UsedInCount > 0 ? item.UsedInCount : (item.IsCraftingItem ? 1 : 0);
		int weightOverride = 0;
		if (weights.ItemWeights.TryGetValue(item.Id, out int overrideWeight)) {
			weightOverride = overrideWeight;
		}
		return (usedInCount * rarityMultiplier) + weightOverride;
	}

	private static double GetRarityMultiplier(string? rarity) {
		if (string.IsNullOrWhiteSpace(rarity)) return 1.0;
		switch (rarity.Trim().ToLowerInvariant()) {
			case "uncommon": return 1.2;
			case "rare": return 1.5;
			case "epic": return 2.0;
			case "legendary": return 2.5;
			default: return 1.0;
		}
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
