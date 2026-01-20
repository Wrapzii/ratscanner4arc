using System;
using System.Globalization;
using System.Windows.Data;

namespace RatScanner.View;

[ValueConversion(typeof(int), typeof(string))]
public class IntToLongPriceConverter : IValueConverter {
	public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
		if (value == null) return "0 CR";

		string text = $"{value:n0}";
		string numberGroupSeparator = NumberFormatInfo.CurrentInfo.NumberGroupSeparator;
		return text.Replace(numberGroupSeparator, RatConfig.ToolTip.DigitGroupingSymbol) + " CR";
	}

	public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
		return 0;
	}
}
