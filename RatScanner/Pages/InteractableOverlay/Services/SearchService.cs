using System;
using System.Collections.Generic;
using System.Linq;

namespace RatScanner.Pages.InteractableOverlay.Services;

public class SearchService {
	public async System.Threading.Tasks.Task<IEnumerable<SearchResult>> SearchItemsAsync(string value) {
		if (string.IsNullOrEmpty(value)) return Enumerable.Empty<SearchResult>();

		Func<ArcRaidersData.ArcItem, SearchResult?> filter = (item) => {
			if (SanitizeSearch(item.Name) == value) return new(item, 5);
			if (SanitizeSearch(item.ShortName) == value) return new(item, 10);
			if (SanitizeSearch(item.Name).StartsWith(value)) return new(item, 20);
			if (SanitizeSearch(item.ShortName).StartsWith(value)) return new(item, 20);
			string[] filters = value.Split(new[] { ' ' });
			if (filters.All(filter => SanitizeSearch(item.Name).Contains(filter))) return new(item, 40);
			if (filters.All(filter => SanitizeSearch(item.ShortName).Contains(filter))) return new(item, 40);
			if (SanitizeSearch(item.Name).Contains(value)) return new(item, 60);
			if (SanitizeSearch(item.ShortName).Contains(value)) return new(item, 60);
			if (value.Length > 3 && SanitizeSearch(item.Id).StartsWith(value)) return new(item, 80);
			if (value.Length > 3 && SanitizeSearch(item.Id).Contains(value)) return new(item, 100);
			return null;
		};

		List<SearchResult> matches = new();
		await System.Threading.Tasks.Task.Run(() => {
			foreach (var item in ArcRaidersData.GetItems()) {
				var match = filter(item);
				if (match?.Data == null) continue;
				matches.Add(match);
			}
		});

		for (int i = 0; i < matches.Count; i++) {
			if (!(matches[i].Data is ArcRaidersData.ArcItem item)) continue;
			matches[i].Score += (item.Name?.Length ?? 0) * 0.002;
		}
		return matches;
	}

	public string SanitizeSearch(string? value) {
		if (string.IsNullOrEmpty(value)) return string.Empty;
		value = value.ToLower().Trim();
		value = value.Replace("-", " ");
		value = new string(value.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
		return value;
	}
}

public class SearchResult {
	public SearchResult(object data, float score) {
		Score = score;
		Data = data;
	}
	public object Data;
	public double Score;
}
