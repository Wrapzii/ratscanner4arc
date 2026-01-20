using RatEye;
using System;

namespace RatScanner.Scan;

/// <summary>
/// Represents a scan result from detecting an Arc Raiders item tooltip (the cream/beige popup when hovering items)
/// </summary>
public class TooltipScan : ItemScan {
	private readonly Vector2 _toolTipPosition;
	
	/// <summary>
	/// The raw OCR text extracted from the tooltip
	/// </summary>
	public string RawOcrText { get; }
	
	/// <summary>
	/// Whether the scan was matched via fuzzy string matching
	/// </summary>
	public bool IsFuzzyMatch { get; }

	public TooltipScan(ArcRaidersData.ArcItem item, Vector2 tooltipPosition, float confidence, string rawOcrText, bool isFuzzyMatch = false, int? duration = null) {
		Item = item;
		_toolTipPosition = tooltipPosition;
		Confidence = confidence;
		RawOcrText = rawOcrText;
		IsFuzzyMatch = isFuzzyMatch;
		IconPath = item.ImageLink ?? RatConfig.Paths.UnknownIcon;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + (duration ?? RatConfig.ToolTip.Duration);
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
