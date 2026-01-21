using RatScanner;
using RatScanner.Scan;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace RatScanner.ViewModel;

internal class MenuVM : INotifyPropertyChanged {
	private RatScannerMain _dataSource;

	public RatScannerMain DataSource {
		get => _dataSource;
		set {
			_dataSource = value;
			OnPropertyChanged();
		}
	}

	public ItemQueue ItemScans => DataSource.ItemScans;

	public ItemScan LastItemScan => ItemScans.LastOrDefault() ?? throw new Exception("ItemQueue is empty!");

	public ArcRaidersData.ArcItem LastItem => LastItemScan.Item;

	public string DiscordLink => ApiManager.GetResource(ApiManager.ResourceType.DiscordLink);

	public string GithubLink => ApiManager.GetResource(ApiManager.ResourceType.GithubLink);

	public string PatreonLink => ApiManager.GetResource(ApiManager.ResourceType.PatreonLink);

	public string WikiLink {
		get {
			string? link = LastItem.WikiLink;
			if (!string.IsNullOrWhiteSpace(link)) return link;
			return $"https://metaforge.app/arc-raiders/database/item/{LastItem.Id}";
		}
	}

	public int ValuePerSlot => LastItem.ValuePerSlot;

	public (RecycleDecision decision, string reason) RecycleRecommendation => LastItem.GetRecycleRecommendation();

	public bool ShouldRecycle => RecycleRecommendation.decision == RecycleDecision.Recycle;
	public bool ShouldKeep => RecycleRecommendation.decision == RecycleDecision.Keep;

	public string RecycleReason => RecycleRecommendation.reason;

	public event PropertyChangedEventHandler PropertyChanged;

	public MenuVM(RatScannerMain ratScanner) {
		DataSource = ratScanner;
		DataSource.PropertyChanged += ModelPropertyChanged;
		ArcRaidersData.AuxDataUpdated += OnAuxDataUpdated;
	}

	protected virtual void OnPropertyChanged(string propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	public void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e) {
		OnPropertyChanged();
	}

	private void OnAuxDataUpdated() {
		try {
			var dispatcher = Application.Current?.Dispatcher;
			if (dispatcher != null && !dispatcher.CheckAccess()) {
				dispatcher.Invoke(() => OnPropertyChanged());
				return;
			}
		} catch {
			// Ignore dispatcher failures
		}
		OnPropertyChanged();
	}

	// Still used in minimal menu
	public string IntToLongPrice(int? value) {
		return value.AsCredits();
	}
}
