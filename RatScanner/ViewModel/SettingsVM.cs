using RatStash;
using System.ComponentModel;
using System.Threading.Tasks;

namespace RatScanner.ViewModel;

internal class SettingsVM : INotifyPropertyChanged {
	public bool EnableNameScan { get; set; }
	public bool EnableAutoNameScan { get; set; }
	public int NameScanLanguage { get; set; }

	public bool EnableTooltipScan { get; set; }
	public Hotkey TooltipScanHotkey { get; set; }

	public string ToolTipDuration { get; set; }
	public int ToolTipMilli { get; set; }

	public bool ShowName { get; set; }
	public bool ShowValue { get; set; }
	public bool ShowValuePerSlot { get; set; }
	public bool ShowRarity { get; set; }
	public bool ShowWeight { get; set; }
	public bool ShowRecycle { get; set; }
	public int Opacity { get; set; }

	public int ScreenWidth { get; set; }
	public int ScreenHeight { get; set; }
	public float ScreenScale { get; set; }
	public bool MinimizeToTray { get; set; }
	public bool AlwaysOnTop { get; set; }
	public bool LogDebug { get; set; }

	// Minimap filters
	public int MapNearbyLootRadius { get; set; }
	public bool ShowMapContainers { get; set; }
	public bool ShowMapArc { get; set; }
	public bool ShowMapEvents { get; set; }
	public bool ShowMapLocations { get; set; }

	// Interactable Overlay
	public bool EnableIneractableOverlay { get; set; }
	public bool BlurBehindSearch { get; set; }
	public Hotkey InteractableOverlayHotkey { get; set; }

	internal SettingsVM() {
		LoadSettings();
	}

	public void LoadSettings() {
		EnableNameScan = RatConfig.NameScan.Enable;
		EnableAutoNameScan = RatConfig.NameScan.EnableAuto;
		NameScanLanguage = (int)RatConfig.NameScan.Language;

		EnableTooltipScan = RatConfig.TooltipScan.Enable;
		TooltipScanHotkey = RatConfig.TooltipScan.Hotkey;

		ToolTipDuration = RatConfig.ToolTip.Duration.ToString();
		ToolTipMilli = RatConfig.ToolTip.Duration;

		ShowName = RatConfig.MinimalUi.ShowName;
		ShowValue = RatConfig.MinimalUi.ShowValue;
		ShowValuePerSlot = RatConfig.MinimalUi.ShowValuePerSlot;
		ShowRarity = RatConfig.MinimalUi.ShowRarity;
		ShowWeight = RatConfig.MinimalUi.ShowWeight;
		ShowRecycle = RatConfig.MinimalUi.ShowRecycle;
		Opacity = RatConfig.MinimalUi.Opacity;

		ScreenWidth = RatConfig.ScreenWidth;
		ScreenHeight = RatConfig.ScreenHeight;
		ScreenScale = RatConfig.ScreenScale;
		MinimizeToTray = RatConfig.MinimizeToTray;
		AlwaysOnTop = RatConfig.AlwaysOnTop;
		LogDebug = RatConfig.LogDebug;

		MapNearbyLootRadius = RatConfig.Map.NearbyLootRadius;
		ShowMapContainers = RatConfig.Map.ShowContainers;
		ShowMapArc = RatConfig.Map.ShowArc;
		ShowMapEvents = RatConfig.Map.ShowEvents;
		ShowMapLocations = RatConfig.Map.ShowLocations;

		EnableIneractableOverlay = RatConfig.Overlay.Search.Enable;
		BlurBehindSearch = RatConfig.Overlay.Search.BlurBehind;
		InteractableOverlayHotkey = RatConfig.Overlay.Search.Hotkey;

		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
	}

	public async Task SaveSettings() {
		bool updateResolution = ScreenWidth != RatConfig.ScreenWidth || ScreenHeight != RatConfig.ScreenHeight;
		bool updateLanguage = RatConfig.NameScan.Language != (Language)NameScanLanguage;

		// Save config
		RatConfig.NameScan.Enable = EnableNameScan;
		RatConfig.NameScan.EnableAuto = EnableAutoNameScan;
		RatConfig.NameScan.Language = (Language)NameScanLanguage;

		RatConfig.TooltipScan.Enable = EnableTooltipScan;
		RatConfig.TooltipScan.Hotkey = TooltipScanHotkey;

		RatConfig.ToolTip.Duration = int.TryParse(ToolTipDuration, out int i) ? i : 0;
		RatConfig.ToolTip.Duration = ToolTipMilli;

		RatConfig.MinimalUi.ShowName = ShowName;
		RatConfig.MinimalUi.ShowValue = ShowValue;
		RatConfig.MinimalUi.ShowValuePerSlot = ShowValuePerSlot;
		RatConfig.MinimalUi.ShowRarity = ShowRarity;
		RatConfig.MinimalUi.ShowWeight = ShowWeight;
		RatConfig.MinimalUi.ShowRecycle = ShowRecycle;
		RatConfig.MinimalUi.Opacity = Opacity;

		RatConfig.Overlay.Search.Enable = EnableIneractableOverlay;
		RatConfig.Overlay.Search.BlurBehind = BlurBehindSearch;
		RatConfig.Overlay.Search.Hotkey = InteractableOverlayHotkey;

		RatConfig.ScreenWidth = ScreenWidth;
		RatConfig.ScreenHeight = ScreenHeight;
		RatConfig.ScreenScale = ScreenScale;
		RatConfig.MinimizeToTray = MinimizeToTray;
		RatConfig.AlwaysOnTop = AlwaysOnTop;
		RatConfig.LogDebug = LogDebug;

		RatConfig.Map.NearbyLootRadius = MapNearbyLootRadius;
		RatConfig.Map.ShowContainers = ShowMapContainers;
		RatConfig.Map.ShowArc = ShowMapArc;
		RatConfig.Map.ShowEvents = ShowMapEvents;
		RatConfig.Map.ShowLocations = ShowMapLocations;

		// Apply config
		PageSwitcher.Instance.Topmost = RatConfig.AlwaysOnTop;
		PageSwitcher.Instance.ResetWindowSize();
		if (updateResolution || updateLanguage) RatScannerMain.Instance.SetupRatEye();

		RatEye.Config.LogDebug = RatConfig.LogDebug;
		RatScannerMain.Instance.HotkeyManager.RegisterHotkeys();

		// Save config to file
		Logger.LogInfo("Saving config...");
		RatConfig.SaveConfig();
		Logger.LogInfo("Config saved!");
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	internal virtual void OnPropertyChanged(string? propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
