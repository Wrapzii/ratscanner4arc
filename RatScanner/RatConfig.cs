using RatStash;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Windows.Input;
using Key = System.Windows.Input.Key;

namespace RatScanner;

internal static class RatConfig {
	[DllImport("user32.dll")]
	private static extern IntPtr MonitorFromPoint([In] Point pt, [In] uint dwFlags);

	[DllImport("Shcore.dll")]
	private static extern IntPtr GetDpiForMonitor([In] IntPtr hmonitor, [In] DpiType dpiType, [Out] out uint dpiX, [Out] out uint dpiY);

	// Version
	public static string Version => Process.GetCurrentProcess().MainModule?.FileVersionInfo.ProductVersion ?? "Unknown";

	public const string SINGLE_INSTANCE_GUID = "{a057bb64-c126-4ef4-a4ed-3037c2e7bc89}";

	// Paths
	internal static class Paths {
		internal static string Base = AppDomain.CurrentDomain.BaseDirectory;
		internal static string Data = Path.Combine(Base, "Data");
		internal static string StaticIcon = Path.Combine(Data, "icons");
		internal static string Locales = Path.Combine(Data, "locales");

		private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "RatScanner");
		internal static readonly string CacheDir = Path.Combine(TempDir, "Cache");
		internal static string TrainedData = Path.Combine(Data, "traineddata");
		internal static string UnknownIcon = Path.Combine(Data, "unknown.png");
		internal static string ConfigFile = Path.Combine(Base, "config.cfg");
		internal static string Debug = Path.Combine(Base, "Debug");
		internal static string Updater = Path.Combine(Base, "RatUpdater.exe");
		internal static string LogFile = Path.Combine(Base, "Log.txt");
	}

	// Name Scan options
	internal static class NameScan {
		internal static bool Enable = true;
		internal static bool EnableAuto = false;
		internal static Language Language = Language.English;
		internal static float ConfWarnThreshold = 0.85f;
		internal static int MarkerScanSize => (int)(50 * GameScale);
		internal static int TextWidth => (int)(600 * GameScale);
	}

	// Icon Scan options
	internal static class IconScan {
		internal static bool Enable = true;
		internal static float ConfWarnThreshold = 0.8f;
		internal static bool ScanRotatedIcons = true;
		internal static int ScanWidth => (int)(GameScale * 896);
		internal static int ScanHeight => (int)(GameScale * 896);
		internal static int SelectionScanSize => (int)(GameScale * 700);
		internal static Hotkey Hotkey = new(new[] { Key.LeftShift }.ToList(), new[] { MouseButton.Left });
		internal static bool UseCachedIcons = true;
	}

	// Tooltip Scan options (for detecting Arc Raiders item tooltips)
	internal static class TooltipScan {
		internal static bool Enable = true;
		internal static int ScanWidth => (int)(GameScale * 500);  // Tooltips are ~400px wide
		internal static int ScanHeight => (int)(GameScale * 600); // Tooltips can be tall
		internal static Hotkey Hotkey = new(new[] { Key.LeftAlt }.ToList(), new[] { MouseButton.Left });
	}

	// ToolTip options
	internal static class ToolTip {
		internal static string DigitGroupingSymbol = ".";
		internal static int Duration = 1500;
	}

	// Minimal UI
	internal static class MinimalUi {
		internal static bool ShowName = true;
		internal static bool ShowValue = true;
		internal static bool ShowValuePerSlot = true;
		internal static bool ShowRarity = true;
		internal static bool ShowWeight = false;
		internal static bool ShowRecycle = true;
		internal static int Opacity = 0;
	}

	// Overlay options
	internal static class Overlay {
		internal static class Search {
			internal static bool Enable = true;
			internal static bool BlurBehind = true;
			internal static Hotkey Hotkey = new(new[] { Key.N, Key.M }.ToList());
		}
	}

	// OAuth2 refresh tokens
	internal static class OAuthRefreshToken {
		internal static string Discord = "";
		internal static string Patreon = "";
	}

	// Other
#if DEBUG
	internal static bool LogDebug {
		get => true;
		set { }
	}
#else
	internal static bool LogDebug = false;
#endif
	internal static bool MinimizeToTray = false;
	internal static bool AlwaysOnTop = true;
	private static int ConfigVersion => 2;

	internal static int ScreenWidth = 1920;
	internal static int ScreenHeight = 1080;
	internal static float ScreenScale = 1f;
	internal static bool SetScreen = false;
	internal static int LastWindowPositionX = int.MinValue;
	internal static int LastWindowPositionY = int.MinValue;
	internal static WindowMode LastWindowMode = WindowMode.Normal;

	internal static float GameScale => RatScannerMain.Instance.RatEyeEngine.Config.ProcessingConfig.Scale;

	private static bool IsSupportedConfigVersion() {
		SimpleConfig config = new(Paths.ConfigFile, "Other");
		int readConfigVersion = config.ReadInt(nameof(ConfigVersion), -1);
		bool isSupportedConfigVersion = ConfigVersion == readConfigVersion;
		if (!isSupportedConfigVersion) Logger.LogWarning("Config version (" + readConfigVersion + ") is not supported!");
		return isSupportedConfigVersion;
	}

	internal static void LoadConfig() {
		bool configFileExists = File.Exists(Paths.ConfigFile);
		bool isSupportedConfigVersion = IsSupportedConfigVersion();
		if (configFileExists && !isSupportedConfigVersion) {
			string message = "Old config version detected!\n\n";
			message += "It will be removed and replaced with a new config file.\n";
			message += "Please make sure to reconfigure your settings after.";
			Logger.ShowMessage(message);

			File.Delete(Paths.ConfigFile);
			TrySetScreenConfig();
			SaveConfig();
		} else if (!configFileExists) {
			TrySetScreenConfig();
			SaveConfig();
		}

		SimpleConfig config = new(Paths.ConfigFile);

		config.Section = nameof(NameScan);
		NameScan.Enable = config.ReadBool(nameof(NameScan.Enable), NameScan.Enable);
		NameScan.EnableAuto = config.ReadBool(nameof(NameScan.EnableAuto), NameScan.EnableAuto);
		NameScan.Language = (Language)config.ReadInt(nameof(NameScan.Language), (int)NameScan.Language);

		config.Section = nameof(IconScan);
		IconScan.Enable = config.ReadBool(nameof(IconScan.Enable), IconScan.Enable);
		IconScan.ScanRotatedIcons = config.ReadBool(nameof(IconScan.ScanRotatedIcons), IconScan.ScanRotatedIcons);
		IconScan.Hotkey = config.ReadHotkey(nameof(IconScan.Hotkey), IconScan.Hotkey);
		IconScan.UseCachedIcons = config.ReadBool(nameof(IconScan.UseCachedIcons), IconScan.UseCachedIcons);

		config.Section = nameof(TooltipScan);
		TooltipScan.Enable = config.ReadBool(nameof(TooltipScan.Enable), TooltipScan.Enable);
		TooltipScan.Hotkey = config.ReadHotkey(nameof(TooltipScan.Hotkey), TooltipScan.Hotkey);

		config.Section = nameof(ToolTip);
		ToolTip.Duration = config.ReadInt(nameof(ToolTip.Duration), ToolTip.Duration);
		ToolTip.DigitGroupingSymbol = config.ReadString(nameof(ToolTip.DigitGroupingSymbol), ToolTip.DigitGroupingSymbol);

		config.Section = nameof(MinimalUi);
		MinimalUi.ShowName = config.ReadBool(nameof(MinimalUi.ShowName), MinimalUi.ShowName);
		MinimalUi.ShowValue = config.ReadBool(nameof(MinimalUi.ShowValue), MinimalUi.ShowValue);
		MinimalUi.ShowValuePerSlot = config.ReadBool(nameof(MinimalUi.ShowValuePerSlot), MinimalUi.ShowValuePerSlot);
		MinimalUi.ShowRarity = config.ReadBool(nameof(MinimalUi.ShowRarity), MinimalUi.ShowRarity);
		MinimalUi.ShowWeight = config.ReadBool(nameof(MinimalUi.ShowWeight), MinimalUi.ShowWeight);
		MinimalUi.ShowRecycle = config.ReadBool(nameof(MinimalUi.ShowRecycle), MinimalUi.ShowRecycle);
		MinimalUi.Opacity = config.ReadInt(nameof(MinimalUi.Opacity), MinimalUi.Opacity);

		config.Section = nameof(Overlay);

		config.Section = nameof(Overlay.Search);
		Overlay.Search.Enable = config.ReadBool(nameof(Overlay.Search.Enable), Overlay.Search.Enable);
		Overlay.Search.BlurBehind = config.ReadBool(nameof(Overlay.Search.BlurBehind), Overlay.Search.BlurBehind);
		Overlay.Search.Hotkey = config.ReadHotkey(nameof(Overlay.Search.Hotkey), Overlay.Search.Hotkey);

		config.Section = nameof(OAuthRefreshToken);
		OAuthRefreshToken.Discord = config.ReadSecureString(nameof(OAuthRefreshToken.Discord), OAuthRefreshToken.Discord);
		OAuthRefreshToken.Patreon = config.ReadSecureString(nameof(OAuthRefreshToken.Patreon), OAuthRefreshToken.Patreon);

		config.Section = "Other";
		if (!SetScreen) {
			ScreenWidth = config.ReadInt(nameof(ScreenWidth), ScreenWidth);
			ScreenHeight = config.ReadInt(nameof(ScreenHeight), ScreenHeight);
			ScreenScale = config.ReadFloat(nameof(ScreenScale), ScreenScale);
		}

		MinimizeToTray = config.ReadBool(nameof(MinimizeToTray), MinimizeToTray);
		AlwaysOnTop = config.ReadBool(nameof(AlwaysOnTop), AlwaysOnTop);
		LogDebug = config.ReadBool(nameof(LogDebug), LogDebug);

		LastWindowPositionX = config.ReadInt(nameof(LastWindowPositionX), LastWindowPositionX);
		LastWindowPositionY = config.ReadInt(nameof(LastWindowPositionY), LastWindowPositionY);
		LastWindowMode = (WindowMode)config.ReadInt(nameof(LastWindowMode), (int)LastWindowMode);
	}

	internal static void SaveConfig() {
		SimpleConfig config = new(Paths.ConfigFile);

		config.Section = nameof(NameScan);
		config.WriteBool(nameof(NameScan.Enable), NameScan.Enable);
		config.WriteBool(nameof(NameScan.EnableAuto), NameScan.EnableAuto);
		config.WriteInt(nameof(NameScan.Language), (int)NameScan.Language);

		config.Section = nameof(IconScan);
		config.WriteBool(nameof(IconScan.Enable), IconScan.Enable);
		config.WriteBool(nameof(IconScan.ScanRotatedIcons), IconScan.ScanRotatedIcons);
		config.WriteHotkey(nameof(IconScan.Hotkey), IconScan.Hotkey);
		config.WriteBool(nameof(IconScan.UseCachedIcons), IconScan.UseCachedIcons);

		config.Section = nameof(TooltipScan);
		config.WriteBool(nameof(TooltipScan.Enable), TooltipScan.Enable);
		config.WriteHotkey(nameof(TooltipScan.Hotkey), TooltipScan.Hotkey);

		config.Section = nameof(ToolTip);
		config.WriteInt(nameof(ToolTip.Duration), ToolTip.Duration);
		config.WriteString(nameof(ToolTip.DigitGroupingSymbol), ToolTip.DigitGroupingSymbol);

		config.Section = nameof(MinimalUi);
		config.WriteBool(nameof(MinimalUi.ShowName), MinimalUi.ShowName);
		config.WriteBool(nameof(MinimalUi.ShowValue), MinimalUi.ShowValue);
		config.WriteBool(nameof(MinimalUi.ShowValuePerSlot), MinimalUi.ShowValuePerSlot);
		config.WriteBool(nameof(MinimalUi.ShowRarity), MinimalUi.ShowRarity);
		config.WriteBool(nameof(MinimalUi.ShowWeight), MinimalUi.ShowWeight);
		config.WriteBool(nameof(MinimalUi.ShowRecycle), MinimalUi.ShowRecycle);
		config.WriteInt(nameof(MinimalUi.Opacity), MinimalUi.Opacity);

		config.Section = nameof(Overlay);

		config.Section = nameof(Overlay.Search);
		config.WriteBool(nameof(Overlay.Search.Enable), Overlay.Search.Enable);
		config.WriteBool(nameof(Overlay.Search.BlurBehind), Overlay.Search.BlurBehind);
		config.WriteHotkey(nameof(Overlay.Search.Hotkey), Overlay.Search.Hotkey);

		config.Section = nameof(OAuthRefreshToken);
		config.WriteSecureString(nameof(OAuthRefreshToken.Discord), OAuthRefreshToken.Discord);
		config.WriteSecureString(nameof(OAuthRefreshToken.Patreon), OAuthRefreshToken.Patreon);

		config.Section = "Other";
		config.WriteInt(nameof(ScreenWidth), ScreenWidth);
		config.WriteInt(nameof(ScreenHeight), ScreenHeight);
		config.WriteFloat(nameof(ScreenScale), ScreenScale);
		config.WriteBool(nameof(MinimizeToTray), MinimizeToTray);
		config.WriteBool(nameof(AlwaysOnTop), AlwaysOnTop);
		config.WriteBool(nameof(LogDebug), LogDebug);
		config.WriteInt(nameof(ConfigVersion), ConfigVersion);
		config.WriteInt(nameof(LastWindowPositionX), LastWindowPositionX);
		config.WriteInt(nameof(LastWindowPositionY), LastWindowPositionY);
		config.WriteInt(nameof(LastWindowMode), (int)LastWindowMode);
	}

	internal static bool ReadFromCache(string key, out string value) {
		byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
		string hash = string.Concat(Array.ConvertAll(hashBytes, b => b.ToString("X2")));

		string path = Path.Combine(Paths.CacheDir, hash + ".data");
		value = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
		return value != string.Empty;
	}

	internal static void WriteToCache(string key, string value) {
		byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
		string hash = string.Concat(Array.ConvertAll(hashBytes, b => b.ToString("X2")));

		string path = Path.Combine(Paths.CacheDir, hash + ".data");
		Directory.CreateDirectory(Paths.CacheDir);
		File.WriteAllText(path, value);
	}

	/// <summary>
	/// Get the current screen config from the primary screen
	/// </summary>
	internal static void TrySetScreenConfig() {
		int width = Screen.PrimaryScreen.Bounds.Width;
		int height = Screen.PrimaryScreen.Bounds.Height;
		double scale = GetScalingForScreen(Screen.PrimaryScreen);

		ScreenWidth = width;
		ScreenHeight = height;
		ScreenScale = (float)scale;
		SetScreen = true;
	}

	public enum DpiType {
		Effective = 0,
		Angular = 1,
		Raw = 2,
	}

	public enum WindowMode {
		Normal = 0,
		Minimal = 1,
		Minimized = 2,
	}

	public static double GetScalingForScreen(Screen screen) {
		Point pointOnScreen = new(screen.Bounds.X + 1, screen.Bounds.Y + 1);
		nint mon = MonitorFromPoint(pointOnScreen, 2 /*MONITOR_DEFAULTTONEAREST*/);
		GetDpiForMonitor(mon, DpiType.Effective, out uint dpiX, out _);
		return dpiX / 96.0;
	}

}
