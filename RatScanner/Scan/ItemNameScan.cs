using RatEye;
using RatEye.Processing;
using System;
using System.Linq;

namespace RatScanner.Scan;

public class ItemNameScan : ItemScan {
	private Vector2 _toolTipPosition;

	public ItemNameScan(Inspection inspection, Vector2 toolTipPosition, int duration) {
		RatStash.Item inspectionItem = inspection.Item;
		Item = ArcRaidersData.GetItemById(inspectionItem.Id)
			?? ArcRaidersData.GetItemByName(inspectionItem.Name)
			?? new ArcRaidersData.ArcItem {
				Id = inspectionItem.Id,
				Name = string.IsNullOrWhiteSpace(inspectionItem.Name) ? "Unknown Item" : inspectionItem.Name,
				ShortName = string.IsNullOrWhiteSpace(inspectionItem.ShortName) ? "Unknown" : inspectionItem.ShortName,
			};
		Confidence = inspection.MarkerConfidence;
		IconPath = inspection.IconPath;
		_toolTipPosition = toolTipPosition;
		DissapearAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + duration;
	}

	public override Vector2 GetToolTipPosition() {
		return _toolTipPosition;
	}
}
