using System;

namespace RatScanner;
public static class Extensions {
	public static string ToShortString(this int value) {
		string str = value.ToString();
		if (str.Length < 4) return str;

		string[] suffixes = new string[] { "", "K", "M", "B", "T", "Q" };

		string digits = str[..3];

		int dotPos = str.Length % 3;
		if (dotPos != 0) digits = digits[..dotPos];

		string suffix = suffixes[(int)Math.Floor((str.Length - 1) / 3f)];
		return $"{digits}{suffix}";
	}
	public static string ToShortString(this int? value) => ToShortString(value ?? 0);

	public static int LevenshteinDistance(this string s, string t) {
		if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
		if (string.IsNullOrEmpty(t)) return s.Length;

		int[] v0 = new int[t.Length + 1];
		int[] v1 = new int[t.Length + 1];

		for (int i = 0; i < v0.Length; i++) v0[i] = i;

		for (int i = 0; i < s.Length; i++) {
			v1[0] = i + 1;

			for (int j = 0; j < t.Length; j++) {
				int cost = (s[i] == t[j]) ? 0 : 1;
				v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
			}

			for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
		}

		return v1[t.Length];
	}
}
