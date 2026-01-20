using RatEye;
using System;

namespace RatScanner.Scan;

public class DefaultItemScan : ItemScan {
	private Vector2 _toolTipPosition = Vector2.Zero;

	public DefaultItemScan() {
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + RatConfig.ToolTip.Duration;
	}

	public DefaultItemScan(ArcRaidersData.ArcItem item) : this(item, Vector2.Zero) { }

	public DefaultItemScan(ArcRaidersData.ArcItem item, Vector2 toolTipPosition, int? duration = null, string? iconPathOverride = null) {
		Item = item;
		Confidence = 1;
		IconPath = iconPathOverride ?? item.ImageLink ?? RatConfig.Paths.UnknownIcon;
		_toolTipPosition = toolTipPosition;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + (duration ?? RatConfig.ToolTip.Duration);
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
