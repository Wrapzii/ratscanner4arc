using RatEye;
using RatScanner.Scan;
using RatStash;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using Tesseract;
using MessageBox = System.Windows.MessageBox;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Size = System.Drawing.Size;
using Timer = System.Threading.Timer;

namespace RatScanner;

public class RatScannerMain : INotifyPropertyChanged {
	private static RatScannerMain _instance = null!;
	internal static RatScannerMain Instance => _instance ??= new RatScannerMain();
	private static readonly HttpClient IconHttpClient = new();

	internal readonly HotkeyManager HotkeyManager;

	private Timer? _marketDBRefreshTimer;
	private Timer? _scanRefreshTimer;

	/// <summary>
	/// Lock for name scanning
	/// </summary>
	/// <remarks>
	/// Lock order: 0
	/// </remarks>
	internal static object NameScanLock = new();

	/// <summary>
	/// Lock for icon scanning
	/// </summary>
	/// <remarks>
	/// Lock order: 1
	/// </remarks>
	internal static object IconScanLock = new();

	/// <summary>
	/// Lock for tooltip scanning
	/// </summary>
	/// <remarks>
	/// Lock order: 2
	/// </remarks>
	internal static object TooltipScanLock = new();

	internal RatEyeEngine RatEyeEngine;
	private Dictionary<string, ulong>? _iconHashes;
	private readonly object _iconHashLock = new();
	private static readonly System.Drawing.Color IconHashBackground = System.Drawing.Color.FromArgb(24, 28, 40);

	public event PropertyChangedEventHandler? PropertyChanged;

	internal ItemQueue ItemScans = new();

	public RatScannerMain() {
		_instance = this;

		// Remove old log
		Logger.Clear();

		Logger.LogInfo("----- RatScanner " + RatConfig.Version + " -----");
		Logger.LogInfo($"Screen Info: {RatConfig.ScreenWidth}x{RatConfig.ScreenHeight} at {RatConfig.ScreenScale * 100}%");

		Logger.LogInfo("Loading Arc Raiders items...");
		var items = ArcRaidersData.GetItems();
		if (items.Length > 0) {
			ItemScans.Enqueue(new DefaultItemScan(items[new Random().Next(items.Length)]));
		} else {
			ItemScans.Enqueue(new DefaultItemScan(new ArcRaidersData.ArcItem { Name = "Unknown Item", ShortName = "Unknown" }));
		}

		Logger.LogInfo("Initializing hotkey manager...");
		HotkeyManager = new HotkeyManager();
		HotkeyManager.UnregisterHotkeys();

		Logger.LogInfo("UI Ready!");

		Logger.LogInfo("Initializing RatEye...");
		SetupRatEye();

		new Thread(() => {
			Thread.Sleep(1000);
			Logger.LogInfo("Checking for updates...");
			// Legacy update check disabled in favor of Velopack
			// CheckForUpdates();

			Logger.LogInfo("Setting up timer routines...");
			_scanRefreshTimer = new Timer(RefreshOverlay, null, 1000, 500);

			Logger.LogInfo("Enabling hotkeys...");
			HotkeyManager.RegisterHotkeys();

			Logger.LogInfo("Ready!");
		}).Start();
	}

	private void CheckForUpdates() {
		string mostRecentVersion = ApiManager.GetResource(ApiManager.ResourceType.ClientVersion);
		if (RatConfig.Version == mostRecentVersion) return;
		Logger.LogInfo("A new version is available: " + mostRecentVersion);

		string forceVersions = ApiManager.GetResource(ApiManager.ResourceType.ClientForceUpdateVersions);
		if (forceVersions.Contains($"[{RatConfig.Version}]")) {
			UpdateRatScanner();
			return;
		}

		string message = "Version " + mostRecentVersion + " is available!\n";
		message += "You are using: " + RatConfig.Version + "\n\n";
		message += "Do you want to install it now?";
		MessageBoxResult result = MessageBox.Show(message, "Rat Scanner Updater", MessageBoxButton.YesNo);
		if (result == MessageBoxResult.Yes) UpdateRatScanner();
	}

	private void UpdateRatScanner() {
		if (!File.Exists(RatConfig.Paths.Updater)) {
			Logger.LogWarning(RatConfig.Paths.Updater + " could not be found!");
			try {
				string updaterLink = ApiManager.GetResource(ApiManager.ResourceType.UpdaterLink);
				ApiManager.DownloadFile(updaterLink, RatConfig.Paths.Updater);
			} catch (Exception e) {
				Logger.LogError("Unable to download updater, please update manually.", e);
				return;
			}
		}

		ProcessStartInfo startInfo = new(RatConfig.Paths.Updater);
		startInfo.UseShellExecute = true;
		startInfo.ArgumentList.Add("--start");
		startInfo.ArgumentList.Add("--update");
		Process.Start(startInfo);
		Environment.Exit(0);
	}

	[MemberNotNull(nameof(RatEyeEngine))]
	internal void SetupRatEye() {
		Directory.CreateDirectory(RatConfig.Paths.Data);
		Directory.CreateDirectory(RatConfig.Paths.StaticIcon);
		Directory.CreateDirectory(RatConfig.Paths.TrainedData);
		EnsureLocalDataAssets();
		_ = CacheArcIconsAsync();
		Config.LogDebug = RatConfig.LogDebug;
		Config.Path.LogFile = "RatEyeLog.txt";
		Config.Path.TesseractLibSearchPath = AppDomain.CurrentDomain.BaseDirectory;
		RatEyeEngine = new RatEyeEngine(GetRatEyeConfig(), RatStashDatabaseFromArcRaiders());
	}

	private void EnsureLocalDataAssets() {
		try {
			if (!File.Exists(RatConfig.Paths.UnknownIcon)) {
				CreateUnknownIcon(RatConfig.Paths.UnknownIcon);
			}
		} catch (Exception e) {
			Logger.LogWarning("Unable to create unknown icon placeholder.", e);
		}

		if (!Directory.EnumerateFiles(RatConfig.Paths.TrainedData, "*.traineddata", SearchOption.AllDirectories).Any()) {
			Logger.LogWarning($"No traineddata files found in '{RatConfig.Paths.TrainedData}'. Attempting to download...");
			_ = DownloadTrainedDataAsync();
		}

		if (!Directory.EnumerateFiles(RatConfig.Paths.StaticIcon, "*.*", SearchOption.AllDirectories).Any()) {
			Logger.LogWarning($"No static icons found in '{RatConfig.Paths.StaticIcon}'. Icon scans will fail until icons are provided.");
		}
	}

	private void CreateUnknownIcon(string path) {
		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RatConfig.Paths.Data);
		using Bitmap bmp = new(64, 64, PixelFormat.Format32bppArgb);
		using Graphics gfx = Graphics.FromImage(bmp);
		gfx.Clear(System.Drawing.Color.FromArgb(32, 32, 32));
		using Pen border = new(System.Drawing.Color.FromArgb(90, 90, 90));
		gfx.DrawRectangle(border, 0, 0, bmp.Width - 1, bmp.Height - 1);
		using Font font = new("Segoe UI", 28, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
		using StringFormat format = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
		gfx.DrawString("?", font, Brushes.White, new System.Drawing.RectangleF(0, 0, bmp.Width, bmp.Height), format);
		bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
	}

	private async System.Threading.Tasks.Task CacheArcIconsAsync() {
		try {
			var items = ArcRaidersData.GetItems();
			if (items.Length == 0) return;

			int downloaded = 0;
			foreach (var item in items) {
				if (string.IsNullOrWhiteSpace(item.ImageLink)) continue;
				string outPath = Path.Combine(RatConfig.Paths.StaticIcon, $"{item.Id}.png");
				if (File.Exists(outPath)) continue;

				try {
					byte[] data = await IconHttpClient.GetByteArrayAsync(item.ImageLink);
					SaveWebImageAsPng(data, outPath);
					downloaded++;
				} catch (Exception e) {
					Logger.LogDebug($"Icon cache failed for '{item.Id}': {e.Message}");
				}
			}

			if (downloaded > 0) {
				Logger.LogInfo($"Icon cache updated: downloaded {downloaded} icons.");
			}
		} catch (Exception e) {
			Logger.LogWarning("Icon cache initialization failed.", e);
		}
	}

	private async System.Threading.Tasks.Task DownloadTrainedDataAsync() {
		try {
			string engTrainedDataPath = Path.Combine(RatConfig.Paths.TrainedData, "eng.traineddata");
			if (File.Exists(engTrainedDataPath)) return;

			// Download English traineddata from Tesseract GitHub
			string url = "https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata";
			Logger.LogInfo("Downloading Tesseract English traineddata for OCR...");
			
			byte[] data = await IconHttpClient.GetByteArrayAsync(url);
			Directory.CreateDirectory(RatConfig.Paths.TrainedData);
			await File.WriteAllBytesAsync(engTrainedDataPath, data);
			
			Logger.LogInfo("Tesseract traineddata downloaded successfully.");
		} catch (Exception e) {
			Logger.LogWarning($"Failed to download traineddata: {e.Message}. Tooltip OCR scanning will not work.", e);
		}
	}

	private void SaveWebImageAsPng(byte[] data, string outPath) {
		using MemoryStream input = new(data);
		BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
		BitmapSource frame = decoder.Frames[0];
		PngBitmapEncoder encoder = new();
		encoder.Frames.Add(BitmapFrame.Create(frame));
		Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? RatConfig.Paths.StaticIcon);
		using FileStream output = File.Open(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
		encoder.Save(output);
	}

	private RatEye.Config GetRatEyeConfig(bool highlighted = true) {
		return new Config() {
			PathConfig = new Config.Path() {
				TrainedData = RatConfig.Paths.TrainedData,
				StaticIcons = RatConfig.Paths.StaticIcon,
			},
			ProcessingConfig = new Config.Processing() {
				Scale = Config.Processing.Resolution2Scale(RatConfig.ScreenWidth, RatConfig.ScreenHeight),
				Language = RatConfig.NameScan.Language,
				IconConfig = new Config.Processing.Icon() {
					UseStaticIcons = true,
					ScanMode = Config.Processing.Icon.ScanModes.TemplateMatching,
				},
				InventoryConfig = new Config.Processing.Inventory() {
					OptimizeHighlighted = highlighted,
				},
			},
		};
	}

	private Database RatStashDatabaseFromArcRaiders() {
		List<Item> rsItems = new();
		foreach (ArcRaidersData.ArcItem i in ArcRaidersData.GetItems()) {
			rsItems.Add(new RatStash.Item() {
				Id = i.Id,
				Name = i.Name,
				ShortName = i.ShortName,

			});
		}
		
		return RatStash.Database.FromItems(rsItems);
	}

	/// <summary>
	/// Perform a name scan at the give position
	/// For Arc Raiders, this delegates to TooltipScan since Arc Raiders uses tooltips instead of Tarkov-style name overlays
	/// </summary>
	/// <param name="position">Position on the screen at which to perform the scan</param>
	internal void NameScan(Vector2 position) {
		// Arc Raiders uses tooltip scanning instead of Tarkov's name overlay system
		Logger.LogDebug("NameScan: delegating to TooltipScan for Arc Raiders");
		TooltipScan(position);
	}

	/// <summary>
	/// Perform a name scan over the entire active screen
	/// </summary>
	internal void NameScanScreen(object? _ = null) {
		lock (NameScanLock) {
			Logger.LogDebug("Name scanning screen");
			Vector2 mousePosition = UserActivityHelper.GetMousePosition();
			System.Drawing.Rectangle bounds = Screen.AllScreens.First(screen => screen.Bounds.Contains(mousePosition)).Bounds;

			Vector2 position = new(bounds.X, bounds.Y);
			Bitmap screenshot = GetScreenshot(position, bounds.Size);

			// Scan the item
			RatEye.Processing.MultiInspection multiInspection = RatEyeEngine.NewMultiInspection(screenshot);

			if (multiInspection.Inspections.Count == 0) {
				Logger.LogDebug("NameScan: no inspections found");
				return;
			}

			foreach (RatEye.Processing.Inspection? inspection in multiInspection.Inspections) {
				float scale = RatEyeEngine.Config.ProcessingConfig.Scale;
				Vector2 toolTipPosition = inspection.MarkerPosition;
				toolTipPosition += position;
				Bitmap marker = RatEyeEngine.Config.ProcessingConfig.InspectionConfig.Marker;
				toolTipPosition += new Vector2(0, (int)(marker.Height * scale));

				ItemNameScan tempNameScan = new(
						inspection,
						toolTipPosition,
						RatConfig.ToolTip.Duration);

				ItemScans.Enqueue(tempNameScan);
				Logger.LogDebug($"NameScan: matched '{tempNameScan.Item.Name}' ({tempNameScan.Item.Id}), conf {tempNameScan.Confidence:0.00}");
			}
			RefreshOverlay();
		}
	}

	/// <summary>
	/// Perform a icon scan at the given position
	/// </summary>
	/// <param name="position">Position on the screen at which to perform the scan</param>
	/// <returns><see langword="true"/> if a item was scanned successfully</returns>
	internal void IconScan(Vector2 position) {
		lock (IconScanLock) {
			Logger.LogDebug("Icon scanning at: " + position);
			Vector2 clickPosition = position;
			int x = position.X - RatConfig.IconScan.ScanWidth / 2;
			int y = position.Y - RatConfig.IconScan.ScanHeight / 2;

			Vector2 screenshotPosition = ClampCaptureToVirtualScreen(new Vector2(x, y), new Size(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight));
			Size size = new(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight);
			Bitmap screenshot = GetScreenshot(screenshotPosition, size);

			// Scan the item
			RatEye.Processing.Inventory inventory = RatEyeEngine.NewInventory(screenshot);
			RatEye.Processing.Icon? icon = inventory.LocateIcon();

			if (icon == null || icon.DetectionConfidence <= 0 || icon.Item == null) {
				if (TryMatchSelectedIcon(clickPosition, out ArcRaidersData.ArcItem matchedItem, out string matchedIconPath)) {
					Logger.LogDebug($"IconScan: cursor match '{matchedItem.Name}'");
					ItemScans.Enqueue(new DefaultItemScan(matchedItem, clickPosition, RatConfig.ToolTip.Duration, matchedIconPath));
					RefreshOverlay();
					return;
				}

				if (icon == null) {
					Logger.LogDebug("IconScan: no icon found");
					EnqueueFallbackScan(clickPosition, "No icon found");
					return;
				}
				if (icon.DetectionConfidence <= 0) {
					Logger.LogDebug($"IconScan: low confidence ({icon.DetectionConfidence:0.00})");
					EnqueueFallbackScan(clickPosition, "Low confidence icon match");
					return;
				}
				Logger.LogDebug("IconScan: icon found but item is null");
				EnqueueFallbackScan(clickPosition, "Icon found but item is null");
				return;
			}

			Vector2 toolTipPosition = position;
			toolTipPosition += icon.Position + icon.ItemPosition;
			toolTipPosition -= new Vector2(RatConfig.IconScan.ScanWidth, RatConfig.IconScan.ScanHeight) / 2;

			ItemIconScan tempIconScan = new(icon, toolTipPosition, RatConfig.ToolTip.Duration);

			ItemScans.Enqueue(tempIconScan);
			Logger.LogDebug($"IconScan: matched '{tempIconScan.Item.Name}' ({tempIconScan.Item.Id}), conf {tempIconScan.Confidence:0.00}");
			RefreshOverlay();
		}
	}

	/// <summary>
	/// Perform a tooltip scan to detect Arc Raiders item tooltips (cream/beige background popups)
	/// </summary>
	/// <param name="position">Position on the screen at which to perform the scan</param>
	internal void TooltipScan(Vector2 position) {
		lock (TooltipScanLock) {
			Logger.LogDebug("Tooltip scanning at: " + position);
			Vector2 clickPosition = position;
			
			// Wait briefly for the game UI tooltip to appear
			Thread.Sleep(100);

			// Scan a larger area around the cursor to find the tooltip
			int scanWidth = RatConfig.TooltipScan.ScanWidth;
			int scanHeight = RatConfig.TooltipScan.ScanHeight;
			
			Bitmap? screenshot = null;
			Vector2? screenshotPosition = null;
			System.Drawing.Rectangle tooltipBounds = System.Drawing.Rectangle.Empty;
			string captureLabel = "";
			
			var vs = SystemInformation.VirtualScreen;
			int largeWidth = Math.Min(vs.Width, scanWidth * 3);
			int largeHeight = Math.Min(vs.Height, scanHeight * 3);
			
			var attempts = new (Vector2 Pos, Size Size, string Label)[] {
				// bias capture strongly to the right of the cursor since tooltips appear to the right
				(new Vector2(position.X + scanWidth / 8, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "right-1"),
				(new Vector2(position.X + scanWidth / 3, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "right-2"),
				(new Vector2(position.X + scanWidth / 2, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "right-3"),
				(new Vector2(position.X, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "right"),
				(new Vector2(position.X - scanWidth / 4, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "center-right"),
				(new Vector2(position.X - scanWidth / 2, position.Y - scanHeight / 2), new Size(scanWidth, scanHeight), "center"),
				(new Vector2(position.X - scanWidth / 2, position.Y - scanHeight), new Size(scanWidth, scanHeight), "up"),
				(new Vector2(position.X - scanWidth / 2, position.Y), new Size(scanWidth, scanHeight), "down"),
				(new Vector2(position.X - largeWidth / 2, position.Y - largeHeight / 2), new Size(largeWidth, largeHeight), "large-center"),
				(new Vector2(position.X, position.Y - largeHeight / 2), new Size(largeWidth, largeHeight), "large-right")
			};

			foreach (var attempt in attempts) {
				Vector2 pos = ClampCaptureToVirtualScreen(attempt.Pos, attempt.Size);
				Bitmap bmp = GetScreenshot(pos, attempt.Size);
				if (TryFindTooltipBounds(bmp, out tooltipBounds) || TryFindLightTooltipBounds(bmp, out tooltipBounds)) {
					screenshot = bmp;
					screenshotPosition = pos;
					captureLabel = attempt.Label;
					break;
				}
				bmp.Dispose();
			}

			// Try to find the tooltip region (cream/beige colored background)
			if (screenshot == null) {
				Logger.LogDebug("TooltipScan: no tooltip found");
				// Fall back to icon scan if no tooltip detected
				IconScan(position);
				return;
			}
			
			Logger.LogDebug($"TooltipScan: found tooltip at {tooltipBounds} (capture: {captureLabel})");
			
			using Bitmap tooltipCrop = screenshot.Clone(tooltipBounds, PixelFormat.Format24bppRgb);
			System.Drawing.Rectangle tooltipRect = new(0, 0, tooltipCrop.Width, tooltipCrop.Height);

			// OCR a generous title band first (item name), then fall back to a larger text region.
			int titleTop = Math.Clamp((int)(tooltipCrop.Height * 0.06), 20, 120);
			int titleHeight = Math.Clamp((int)(tooltipCrop.Height * 0.18), 60, 110);
			System.Drawing.Rectangle titleRegion = new(
				8,
				titleTop,
				Math.Max(1, tooltipCrop.Width - 16),
				titleHeight
			);
			titleRegion = System.Drawing.Rectangle.Intersect(titleRegion, tooltipRect);
			Logger.LogDebug($"TooltipScan: title region = {titleRegion}");
			
			int textRegionTop = 50; // below the dark ACTIONS header
			int maxTextHeight = Math.Min(320, tooltipCrop.Height - 50);
			System.Drawing.Rectangle textRegion = new(
				8,
				textRegionTop,
				Math.Max(1, tooltipCrop.Width - 16),
				Math.Max(1, maxTextHeight)
			);
			textRegion = System.Drawing.Rectangle.Intersect(textRegion, tooltipRect);
			Logger.LogDebug($"TooltipScan: text region = {textRegion}");
			
			if (textRegion.Width < 20 || textRegion.Height < 20) {
				Logger.LogDebug("TooltipScan: text region too small");
				IconScan(position);
				return;
			}
			
			using Bitmap textImage = tooltipCrop.Clone(textRegion, PixelFormat.Format24bppRgb);
			using Bitmap processedImage = PreprocessForOcr(textImage);
			Bitmap? titleImage = null;
			Bitmap? titleProcessed = null;
			if (titleRegion.Width >= 20 && titleRegion.Height >= 20) {
				titleImage = tooltipCrop.Clone(titleRegion, PixelFormat.Format24bppRgb);
				titleProcessed = PreprocessForOcr(titleImage);
			}
			
			// Save debug images if debug logging is enabled
			if (RatConfig.LogDebug) {
				try {
					Directory.CreateDirectory(RatConfig.Paths.Debug);
					string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
					screenshot.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_full.png"), System.Drawing.Imaging.ImageFormat.Png);
					tooltipCrop.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_crop.png"), System.Drawing.Imaging.ImageFormat.Png);
					textImage.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_name.png"), System.Drawing.Imaging.ImageFormat.Png);
					processedImage.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_processed.png"), System.Drawing.Imaging.ImageFormat.Png);
					if (titleImage != null && titleProcessed != null) {
						titleImage.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_title.png"), System.Drawing.Imaging.ImageFormat.Png);
						titleProcessed.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_title_processed.png"), System.Drawing.Imaging.ImageFormat.Png);
					}
				} catch { /* Ignore debug save errors */ }
			}
			
			// Perform OCR and extract candidates from ALL OCR text before matching.
			string ocrTitle = "";
			string ocrText = "";
			string titleCandidate = "";
			if (titleProcessed != null) {
				using Bitmap titleScaled = UpscaleForOcr(titleProcessed, 2);
				ocrTitle = PerformOcr(titleScaled, pageSegMode: PageSegMode.SingleBlock, whitelist: "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -'");
				Logger.LogDebug($"TooltipScan: OCR title result = '{ocrTitle}'");
				titleCandidate = ExtractTitleFromTitleRegion(ocrTitle);
				Logger.LogDebug($"TooltipScan: title candidate (title region) = '{titleCandidate}'");
			}
			ocrText = PerformOcr(processedImage, pageSegMode: PageSegMode.Auto);
			Logger.LogDebug($"TooltipScan: OCR result = '{ocrText}'");
			if (string.IsNullOrWhiteSpace(titleCandidate)) {
				titleCandidate = ExtractLikelyItemTitle(ocrText);
				Logger.LogDebug($"TooltipScan: title candidate (text region) = '{titleCandidate}'");
			}

			titleProcessed?.Dispose();
			titleImage?.Dispose();
			
			// Try OCR match
			bool hasOcrMatch = false;
			ArcRaidersData.ArcItem ocrItem = new();
			float ocrConfidence = 0f;
			bool ocrFuzzy = false;
			string matchedOcrLine = "";
			hasOcrMatch = TryMatchItemFromOcr(ocrTitle, ocrText, out ocrItem, out ocrConfidence, out ocrFuzzy, out matchedOcrLine);
			if (hasOcrMatch) {
				Logger.LogDebug($"TooltipScan: OCR matched '{ocrItem.Name}' (conf: {ocrConfidence:0.00}, fuzzy: {ocrFuzzy}, line: '{matchedOcrLine}')");
			}

			// Try icon hash match with debugging
			// Decide which result to use (prefer OCR, then hover icon, then hash)
			bool hasResult = false;
			ArcRaidersData.ArcItem finalItem = new();
			float finalConfidence = 0f;
			bool finalFuzzy = false;

			if (hasOcrMatch) {
				Logger.LogDebug("TooltipScan: Using OCR match");
				finalItem = ocrItem;
				finalConfidence = ocrConfidence;
				finalFuzzy = ocrFuzzy;
				hasResult = true;
			} else if (TryMatchSelectedIcon(clickPosition, out ArcRaidersData.ArcItem hoverItem, out string hoverIconPath)) {
				Logger.LogDebug($"TooltipScan: Using hover icon match '{hoverItem.Name}'");
				ItemScans.Enqueue(new DefaultItemScan(hoverItem, clickPosition, RatConfig.ToolTip.Duration, hoverIconPath));
				RefreshOverlay();
				screenshot.Dispose();
				return;
			} else {
				bool hasHashMatch = TryMatchSelectedIconHash(clickPosition, captureLabel, out ArcRaidersData.ArcItem hashItem, out float hashConfidence, out string hashIconPath);
				if (hasHashMatch) {
					Logger.LogDebug($"TooltipScan: Hash matched '{hashItem.Name}' (conf: {hashConfidence:0.00})");
					finalItem = hashItem;
					finalConfidence = hashConfidence;
					finalFuzzy = false;
					hasResult = true;
				}
			}

			if (screenshot == null) return;
			// if (tooltipBounds == System.Drawing.Rectangle.Empty) return; // Allow result even if bounds logic was weird, though logic above requires bounds to set hasResult essentially via OCR text region checks mostly.
            // Actually, hasResult is set if OCR or Icon or Hash matched.
            
			if (hasResult) {
				// Use the cursor position for the overlay instead of the detected tooltip position.
				// This ensures the overlay is always visible near where the user clicked/hovered.
				// Offset slightly (15px) to not obstruct the mouse cursor.
				Vector2 tooltipPosition = new(clickPosition.X + 15, clickPosition.Y + 15);

				string rawOcr = string.IsNullOrWhiteSpace(matchedOcrLine) ? titleCandidate : matchedOcrLine;
				TooltipScan scan = new(finalItem, tooltipPosition, finalConfidence, rawOcr, finalFuzzy);
				ItemScans.Enqueue(scan);
				Logger.LogDebug($"TooltipScan: SUCCESS - Final result: '{finalItem.Name}'");
				RefreshOverlay();
			} else {
				Logger.LogDebug("TooltipScan: no OCR/icon match; trying IconScan fallback");
				IconScan(position);
			}

			screenshot.Dispose();
		}
	}

	private static Vector2 ClampCaptureToVirtualScreen(Vector2 desiredTopLeft, Size size) {
		var vs = SystemInformation.VirtualScreen;
		int minX = vs.Left;
		int minY = vs.Top;
		int maxX = vs.Right - size.Width;
		int maxY = vs.Bottom - size.Height;
		if (maxX < minX) maxX = minX;
		if (maxY < minY) maxY = minY;
		int x = (int)desiredTopLeft.X;
		int y = (int)desiredTopLeft.Y;
		x = Math.Clamp(x, minX, maxX);
		y = Math.Clamp(y, minY, maxY);
		return new Vector2(x, y);
	}

	/// <summary>
	/// Find the bounds of the tooltip (cream/beige colored region) in the screenshot
	/// </summary>
	private static bool TryFindTooltipBounds(Bitmap bitmap, out System.Drawing.Rectangle bounds) {
		bounds = System.Drawing.Rectangle.Empty;
		int width = bitmap.Width;
		int height = bitmap.Height;

		BitmapData bmpData = bitmap.LockBits(
			new System.Drawing.Rectangle(0, 0, width, height),
			ImageLockMode.ReadOnly,
			PixelFormat.Format24bppRgb);

		try {
			int stride = bmpData.Stride;
			int bytesPerPixel = 3;
			int byteCount = stride * height;
			byte[] pixels = new byte[byteCount];
			System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

			bool IsBg(int x, int y) {
				int offset = y * stride + x * bytesPerPixel;
				byte b = pixels[offset];
				byte g = pixels[offset + 1];
				byte r = pixels[offset + 2];
				return IsTooltipBackgroundColor(r, g, b);
			}

			bool[] visited = new bool[width * height];
			Queue<int> q = new();

			int bestCount = 0;
			double bestScore = double.NegativeInfinity;
			System.Drawing.Rectangle bestBounds = System.Drawing.Rectangle.Empty;
			int startX = Math.Max(0, (int)(width * 0.5));
			for (int y0 = 20; y0 < height - 20; y0 += 4) {
				for (int x0 = width - 2; x0 >= startX; x0 -= 3) {
					int idx0 = y0 * width + x0;
					if (visited[idx0]) continue;
					if (!IsBg(x0, y0)) continue;
					
					int minX = x0, minY = y0, maxX = x0, maxY = y0;
					int count = 0;
					long sumX = 0;
					
					q.Enqueue(idx0);
					visited[idx0] = true;

					while (q.Count > 0) {
						int idx = q.Dequeue();
						int y = idx / width;
						int x = idx - y * width;
						
						count++;
						sumX += x;
						if (x < minX) minX = x;
						if (y < minY) minY = y;
						if (x > maxX) maxX = x;
						if (y > maxY) maxY = y;

						void EnqueueIf(int nx, int ny) {
							if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
							int nidx = ny * width + nx;
							if (visited[nidx]) return;
							if (!IsBg(nx, ny)) return;
							visited[nidx] = true;
							q.Enqueue(nidx);
						}

						EnqueueIf(x + 1, y);
						EnqueueIf(x - 1, y);
						EnqueueIf(x, y + 1);
						EnqueueIf(x, y - 1);
					}

					if (count < 1500) continue;
					double centerX = sumX / (double)count;
					double rightBias = centerX / width;
					double score = count * (0.6 + rightBias);
					if (score > bestScore) {
						bestScore = score;
						bestCount = count;
						bestBounds = System.Drawing.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
					}
				}
			}

			if (bestCount == 0) return false;
			bounds = bestBounds;
			// Expand slightly to include header/badges
			bounds.Inflate(10, 14);
			bounds = System.Drawing.Rectangle.Intersect(bounds, new System.Drawing.Rectangle(0, 0, width, height));

			// Extend right edge if beige background continues (prevents cropping the name)
			int extendRight = bounds.Right;
			int rightLimit = Math.Min(width - 1, bounds.Right + 260);
			int bandTop = Math.Min(bounds.Bottom - 20, bounds.Top + 60);
			int bandBottom = Math.Min(bounds.Bottom - 10, bounds.Top + 260);
			if (bandBottom <= bandTop) {
				bandTop = bounds.Top + 10;
				bandBottom = bounds.Bottom - 10;
			}
			for (int x = bounds.Right; x <= rightLimit; x += 2) {
				int bgHits = 0;
				int samples = 0;
				for (int y = bandTop; y < bandBottom; y += 4) {
					samples++;
					if (IsBg(x, y)) bgHits++;
				}
				if (samples > 0 && (bgHits / (double)samples) >= 0.55) {
					extendRight = x;
				} else if (extendRight > bounds.Right + 6) {
					break;
				}
			}
			if (extendRight > bounds.Right) {
				bounds = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, Math.Min(width, extendRight + 1), bounds.Bottom);
			}
			if (bounds.Width < 120 || bounds.Height < 70) return false;
			if (bounds.Width > 1100 || bounds.Height > 1000) return false;
			float ratio = bounds.Width / (float)bounds.Height;
			if (ratio > 3.2f) return false;

			return true;
		} finally {
			bitmap.UnlockBits(bmpData);
		}
	}

	/// <summary>
	/// Fallback: find large, bright tooltip rectangle (near-white) when beige detection fails.
	/// </summary>
	private static bool TryFindLightTooltipBounds(Bitmap bitmap, out System.Drawing.Rectangle bounds) {
		bounds = System.Drawing.Rectangle.Empty;
		int width = bitmap.Width;
		int height = bitmap.Height;

		BitmapData bmpData = bitmap.LockBits(
			new System.Drawing.Rectangle(0, 0, width, height),
			ImageLockMode.ReadOnly,
			PixelFormat.Format24bppRgb);

		try {
			int stride = bmpData.Stride;
			int bytesPerPixel = 3;
			int byteCount = stride * height;
			byte[] pixels = new byte[byteCount];
			System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

			bool IsLightBg(int x, int y) {
				int offset = y * stride + x * bytesPerPixel;
				byte b = pixels[offset];
				byte g = pixels[offset + 1];
				byte r = pixels[offset + 2];
				int max = Math.Max(r, Math.Max(g, b));
				int min = Math.Min(r, Math.Min(g, b));
				int lum = (r * 299 + g * 587 + b * 114) / 1000;
				return lum >= 210 && (max - min) <= 22;
			}

			bool[] visited = new bool[width * height];
			Queue<int> q = new();

			int bestCount = 0;
			double bestScore = double.NegativeInfinity;
			System.Drawing.Rectangle bestBounds = System.Drawing.Rectangle.Empty;

			int startX = Math.Max(0, (int)(width * 0.4));
			for (int y0 = 20; y0 < height - 20; y0 += 4) {
				for (int x0 = width - 2; x0 >= startX; x0 -= 3) {
					int idx0 = y0 * width + x0;
					if (visited[idx0]) continue;
					if (!IsLightBg(x0, y0)) continue;

					int minX = x0, minY = y0, maxX = x0, maxY = y0;
					int count = 0;
					long sumX = 0;

					q.Enqueue(idx0);
					visited[idx0] = true;

					while (q.Count > 0) {
						int idx = q.Dequeue();
						int y = idx / width;
						int x = idx - y * width;

						count++;
						sumX += x;
						if (x < minX) minX = x;
						if (y < minY) minY = y;
						if (x > maxX) maxX = x;
						if (y > maxY) maxY = y;

						void EnqueueIf(int nx, int ny) {
							if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
							int nidx = ny * width + nx;
							if (visited[nidx]) return;
							if (!IsLightBg(nx, ny)) return;
							visited[nidx] = true;
							q.Enqueue(nidx);
						}

						EnqueueIf(x + 1, y);
						EnqueueIf(x - 1, y);
						EnqueueIf(x, y + 1);
						EnqueueIf(x, y - 1);
					}

					if (count < 1500) continue;
					double centerX = sumX / (double)count;
					double rightBias = centerX / width;
					double score = count * (0.6 + rightBias);
					if (score > bestScore) {
						bestScore = score;
						bestCount = count;
						bestBounds = System.Drawing.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
					}
				}
			}

			if (bestCount == 0) return false;
			bounds = bestBounds;
			bounds.Inflate(10, 14);
			bounds = System.Drawing.Rectangle.Intersect(bounds, new System.Drawing.Rectangle(0, 0, width, height));

			if (bounds.Width < 120 || bounds.Height < 70) return false;
			if (bounds.Width > 1400 || bounds.Height > 1200) return false;
			float ratio = bounds.Width / (float)bounds.Height;
			if (ratio > 3.2f) return false;

			return true;
		} finally {
			bitmap.UnlockBits(bmpData);
		}
	}

	/// <summary>
	/// Check if a color matches the Arc Raiders tooltip background (cream/beige)
	/// </summary>
	private static bool IsTooltipBackgroundColor(byte r, byte g, byte b) {
		// Arc Raiders tooltip has a cream/beige background
		// Primary tooltip color: approximately RGB(235, 225, 205) to RGB(245, 235, 215)
		// Also detect lighter/darker variants
		
		int max = Math.Max(r, Math.Max(g, b));
		int min = Math.Min(r, Math.Min(g, b));
		int diff = max - min;
		int lum = (r * 299 + g * 587 + b * 114) / 1000;
		
		// Check for cream/beige tones (light background with low saturation) and avoid pure white highlights
		bool nearWhite = lum > 240 && diff < 15;
		bool isLight = lum >= 205 && lum <= 235 && diff < 50;
		bool isCream = r >= 210 && g >= 200 && b >= 175 && b <= 230 && diff < 55;
		
		return !nearWhite && (isLight || isCream);
	}

	private bool TryMatchTooltipIconHash(Bitmap screenshot, System.Drawing.Rectangle tooltipBounds, out ArcRaidersData.ArcItem matchedItem, out float confidence, out string matchedIconPath, string captureLabel) {
		matchedItem = new ArcRaidersData.ArcItem();
		matchedIconPath = "";
		confidence = 0f;

		EnsureIconHashes();
		if (_iconHashes == null || _iconHashes.Count == 0) {
			Logger.LogDebug("TryMatchTooltipIconHash: no icon hashes loaded");
			return false;
		}

		// Search for the tooltip icon in the top-left area (below ACTIONS header, left side)
		int minSize = Math.Max(48, (int)(RatConfig.GameScale * 56));
		int maxSize = Math.Max(minSize + 12, (int)(RatConfig.GameScale * 100));
		int step = Math.Max(3, (int)(RatConfig.GameScale * 3));

		// Icon is typically at top-left of tooltip, below the "ACTIONS" header
		// Searching approximately: X: left+10 to left+200, Y: top+60 to top+200
		System.Drawing.Rectangle searchArea = new(
			Math.Max(tooltipBounds.Left + 10, 0),
			Math.Max(tooltipBounds.Top + 60, 0),
			Math.Min(190, tooltipBounds.Width - 20),
			Math.Min(140, tooltipBounds.Height - 70)
		);
		searchArea = System.Drawing.Rectangle.Intersect(searchArea, new System.Drawing.Rectangle(0, 0, screenshot.Width, screenshot.Height));
		Logger.LogDebug($"TryMatchTooltipIconHash: searchArea={searchArea}, minSize={minSize}, maxSize={maxSize}");
		if (searchArea.Width < minSize || searchArea.Height < minSize) {
			Logger.LogDebug("TryMatchTooltipIconHash: search area too small");
			return false;
		}

		string? bestId = null;
		int bestDistance = int.MaxValue;
		float bestScore = 0f;
		System.Drawing.Rectangle bestRect = System.Drawing.Rectangle.Empty;
		ulong bestHash = 0;
		Bitmap? bestIconCrop = null;
		var topMatches = new List<(string id, int distance, float score)>();

		int candidatesFound = 0;
		int[] sizes = new[] { minSize, (minSize + maxSize) / 2, maxSize };
		foreach (int size in sizes) {
			for (int y = searchArea.Top; y <= searchArea.Bottom - size; y += step) {
				for (int x = searchArea.Left; x <= searchArea.Right - size; x += step) {
					System.Drawing.Rectangle rect = new(x, y, size, size);
					if (!LooksLikeIconBox(screenshot, rect)) continue;

					candidatesFound++;
					System.Drawing.Rectangle inner = rect;
					inner.Inflate(-2, -2);
					if (inner.Width <= 0 || inner.Height <= 0) continue;

					Bitmap iconCrop = screenshot.Clone(inner, PixelFormat.Format24bppRgb);
					ulong hash = ComputeIconHash(iconCrop);

					foreach (var entry in _iconHashes) {
						int distance = HammingDistance(hash, entry.Value);
						float score = 1.0f - (distance / 64f);
						
						// Track top 5 matches for debugging
						if (topMatches.Count < 5 || distance < topMatches[topMatches.Count - 1].distance) {
							topMatches.Add((entry.Key, distance, score));
							topMatches.Sort((a, b) => a.distance.CompareTo(b.distance));
							if (topMatches.Count > 5) topMatches.RemoveAt(5);
						}

						if (distance < bestDistance) {
							bestDistance = distance;
							bestId = entry.Key;
							bestRect = rect;
							bestScore = score;
							bestHash = hash;
							bestIconCrop?.Dispose();
							bestIconCrop = iconCrop;
						} else {
							iconCrop.Dispose();
						}
					}
				}
			}
		}

		Logger.LogDebug($"TryMatchTooltipIconHash: found {candidatesFound} icon candidates");
		Logger.LogDebug($"TryMatchTooltipIconHash: top 5 matches:");
		foreach (var match in topMatches) {
			var item = ArcRaidersData.GetItemById(match.id);
			Logger.LogDebug($"  - {match.id} ({item?.Name ?? "unknown"}): distance={match.distance}, score={match.score:0.00}");
		}

		if (bestId == null) {
			Logger.LogDebug("TryMatchTooltipIconHash: no icon found");
			bestIconCrop?.Dispose();
			return false;
		}
		
		Logger.LogDebug($"TryMatchTooltipIconHash: bestMatch='{bestId}', distance={bestDistance}, score={bestScore:0.00}, hash=0x{bestHash:X16}");
		
		// Save debug image if enabled
		if (RatConfig.LogDebug && bestIconCrop != null) {
			try {
				string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
				bestIconCrop.Save(Path.Combine(RatConfig.Paths.Debug, $"tooltip_{timestamp}_{captureLabel}_icon_{bestId}.png"), System.Drawing.Imaging.ImageFormat.Png);
			} catch { /* Ignore debug save errors */ }
		}
		bestIconCrop?.Dispose();
		
		if (bestDistance > 20) {
			Logger.LogDebug($"TryMatchTooltipIconHash: distance too high ({bestDistance} > 20), rejecting match");
			return false;
		}

		matchedItem = ArcRaidersData.GetItemById(bestId) ?? new ArcRaidersData.ArcItem { Id = bestId, Name = bestId, ShortName = bestId };
		matchedIconPath = Path.Combine(RatConfig.Paths.StaticIcon, $"{bestId}.png");
		confidence = Math.Max(0.3f, bestScore);
		return true;
	}

	private static bool LooksLikeIconBox(Bitmap bmp, System.Drawing.Rectangle rect) {
		int left = rect.Left;
		int right = rect.Right - 1;
		int top = rect.Top;
		int bottom = rect.Bottom - 1;
		if (left < 0 || top < 0 || right >= bmp.Width || bottom >= bmp.Height) return false;

		// Sample border vs interior luminance
		int borderLum = 0;
		int borderCount = 0;
		int innerLum = 0;
		int innerCount = 0;

		for (int i = 0; i < 5; i++) {
			int fx = left + (i * (rect.Width - 1) / 4);
			int fy = top + (i * (rect.Height - 1) / 4);
			borderLum += GetLuminance(bmp.GetPixel(fx, top)); borderCount++;
			borderLum += GetLuminance(bmp.GetPixel(fx, bottom)); borderCount++;
			borderLum += GetLuminance(bmp.GetPixel(left, fy)); borderCount++;
			borderLum += GetLuminance(bmp.GetPixel(right, fy)); borderCount++;
		}

		int cx = left + rect.Width / 2;
		int cy = top + rect.Height / 2;
		innerLum += GetLuminance(bmp.GetPixel(cx, cy)); innerCount++;
		innerLum += GetLuminance(bmp.GetPixel(cx - rect.Width / 4, cy)); innerCount++;
		innerLum += GetLuminance(bmp.GetPixel(cx + rect.Width / 4, cy)); innerCount++;
		innerLum += GetLuminance(bmp.GetPixel(cx, cy - rect.Height / 4)); innerCount++;
		innerLum += GetLuminance(bmp.GetPixel(cx, cy + rect.Height / 4)); innerCount++;

		if (borderCount == 0 || innerCount == 0) return false;
		int avgBorder = borderLum / borderCount;
		int avgInner = innerLum / innerCount;
		return avgInner - avgBorder > 20;
	}

	private static int GetLuminance(System.Drawing.Color c) {
		return (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
	}

	/// <summary>
	/// Preprocess an image for OCR (enhance contrast, convert to grayscale, etc.)
	/// </summary>
	private static Bitmap PreprocessForOcr(Bitmap source) {
		// Scale up for better OCR accuracy
		int scale = 3;
		Bitmap result = new(source.Width * scale, source.Height * scale, PixelFormat.Format24bppRgb);
		
		using (Graphics gfx = Graphics.FromImage(result)) {
			gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
			gfx.DrawImage(source, 0, 0, result.Width, result.Height);
		}
		
		// Convert to grayscale and apply Otsu-like thresholding
		BitmapData bmpData = result.LockBits(
			new System.Drawing.Rectangle(0, 0, result.Width, result.Height),
			ImageLockMode.ReadWrite,
			PixelFormat.Format24bppRgb);
		
		try {
			int stride = bmpData.Stride;
			int bytesPerPixel = 3;
			int byteCount = stride * result.Height;
			byte[] pixels = new byte[byteCount];
			System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);
			
			// First pass: compute histogram and find optimal threshold
			int[] histogram = new int[256];
			for (int y = 0; y < result.Height; y++) {
				for (int x = 0; x < result.Width; x++) {
					int offset = y * stride + x * bytesPerPixel;
					byte b = pixels[offset];
					byte g = pixels[offset + 1];
					byte r = pixels[offset + 2];
					int gray = (r * 299 + g * 587 + b * 114) / 1000;
					histogram[gray]++;
				}
			}
			
			// Simple Otsu threshold calculation
			int totalPixels = result.Width * result.Height;
			int sum = 0;
			for (int i = 0; i < 256; i++) sum += i * histogram[i];
			
			int sumB = 0, wB = 0;
			float maxVariance = 0;
			int threshold = 128;  // Default threshold
			
			for (int t = 0; t < 256; t++) {
				wB += histogram[t];
				if (wB == 0) continue;
				int wF = totalPixels - wB;
				if (wF == 0) break;
				
				sumB += t * histogram[t];
				float mB = sumB / (float)wB;
				float mF = (sum - sumB) / (float)wF;
				float variance = wB * wF * (mB - mF) * (mB - mF);
				
				if (variance > maxVariance) {
					maxVariance = variance;
					threshold = t;
				}
			}
			
			// Adjust threshold - Arc Raiders item names are dark text on light background
			// Use a threshold slightly below the computed one to capture the text better
			threshold = Math.Max(80, threshold - 20);
			Logger.LogDebug($"PreprocessForOcr: using threshold {threshold}");
			
			// Second pass: apply threshold
			for (int y = 0; y < result.Height; y++) {
				for (int x = 0; x < result.Width; x++) {
					int offset = y * stride + x * bytesPerPixel;
					byte b = pixels[offset];
					byte g = pixels[offset + 1];
					byte r = pixels[offset + 2];
					
					int gray = (r * 299 + g * 587 + b * 114) / 1000;
					
					// Dark text (below threshold) becomes black, light background becomes white
					byte newValue = gray < threshold ? (byte)0 : (byte)255;
					
					pixels[offset] = newValue;
					pixels[offset + 1] = newValue;
					pixels[offset + 2] = newValue;
				}
			}
			
			System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, byteCount);
		} finally {
			result.UnlockBits(bmpData);
		}
		
		return result;
	}

	/// <summary>
	/// Perform OCR on the given image using Tesseract
	/// </summary>
	private string PerformOcr(Bitmap image, PageSegMode pageSegMode = PageSegMode.Auto, string? whitelist = null) {
		try {
			string trainedDataPath = RatConfig.Paths.TrainedData;
			if (!Directory.Exists(trainedDataPath) || 
			    !Directory.EnumerateFiles(trainedDataPath, "*.traineddata").Any()) {
				Logger.LogWarning("No traineddata found for OCR");
				return "";
			}
			
			using var engine = new TesseractEngine(trainedDataPath, "eng", EngineMode.Default);
			engine.SetVariable("tessedit_char_whitelist", whitelist ?? "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			engine.SetVariable("preserve_interword_spaces", "1");
			
			using var pix = Tesseract.PixConverter.ToPix(image);
			using var page = engine.Process(pix, pageSegMode);
			
			string text = page.GetText();
			
			// Clean up the text (preserve newlines for line scoring)
			text = text.Replace("\r\n", "\n").Replace("\r", "\n");
			text = Regex.Replace(text, @"[\t\f\v]+", " ");
			text = Regex.Replace(text, @"[ ]{2,}", " ");
			text = Regex.Replace(text, @"\n{3,}", "\n\n");
			text = text.Trim();
			
			return text;
		} catch (Exception e) {
			Logger.LogWarning($"OCR failed: {e.Message}", e);
			return "";
		}
	}

	private static Bitmap UpscaleForOcr(Bitmap src, int scale) {
		int w = Math.Max(1, src.Width * scale);
		int h = Math.Max(1, src.Height * scale);
		Bitmap enlarged = new(w, h, PixelFormat.Format24bppRgb);
		using Graphics gfx = Graphics.FromImage(enlarged);
		gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
		gfx.DrawImage(src, 0, 0, w, h);
		return enlarged;
	}

	private static string ExtractLikelyItemTitle(string ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) return "";

		// Prefer uppercase-heavy lines (Arc item titles are rendered in uppercase).
		string[] rawLines = ocrText
			.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(l => l.Trim())
			.Where(l => l.Length > 0)
			.ToArray();

		string bestLine = "";
		double bestScore = double.NegativeInfinity;

		foreach (string line in rawLines) {
			string cleaned = Regex.Replace(line, @"[^A-Za-z0-9\s\-']", " ");
			cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
			if (cleaned.Length < 4) continue;

			int letterCount = cleaned.Count(char.IsLetter);
			if (letterCount < 3) continue;

			if (Regex.IsMatch(cleaned, @"\b(Has|Durability|Ammo|Magazine|Firing|Armor|Special|Penetration|Type|Category|Equipment)\b", RegexOptions.IgnoreCase)) {
				continue;
			}

			int alpha = 0, upper = 0, lower = 0;
			foreach (char ch in cleaned) {
				if (!char.IsLetter(ch)) continue;
				alpha++;
				if (char.IsUpper(ch)) upper++;
				if (char.IsLower(ch)) lower++;
			}
			if (alpha < 3) continue;

			double upperRatio = upper / (double)Math.Max(1, alpha);
			double lengthScore = Math.Min(1.0, cleaned.Length / 18.0);
			double penalty = 0;
			string lowerLine = cleaned.ToLowerInvariant();
			if (lowerLine.Contains("fires") || lowerLine.Contains("has ") || lowerLine.Contains("increased") || lowerLine.Contains("reduced") || lowerLine.Contains("damage") || lowerLine.Contains("accuracy") || lowerLine.Contains("headshot") || lowerLine.Contains("projectile") || lowerLine.Contains("detonate") || lowerLine.Contains("recovery") || lowerLine.Contains("magazine") || lowerLine.Contains("durability")) {
				penalty += 0.8;
			}
			if (Regex.IsMatch(lowerLine, @"\b(i|ii|iii|iv|v|vi|vii|viii|ix|x)\b\s*$")) {
				lengthScore += 0.15;
			}

			double score = (upperRatio * 2.0) + lengthScore - penalty;
			if (score > bestScore) {
				bestScore = score;
				bestLine = cleaned;
			}
		}

		if (string.IsNullOrWhiteSpace(bestLine)) {
			bestLine = Regex.Replace(ocrText, @"\s+", " ").Trim();
		}

		// If the OCR didn't preserve line breaks, trim to the leading "title-like" tokens.
		string[] tokens = bestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		List<string> kept = new();
		foreach (string tok in tokens) {
			string t = tok.Trim('-', '\'', '"');
			if (t.Length == 0) continue;
			string tl = t.ToLowerInvariant();
			if (Regex.IsMatch(tl, @"^(i|ii|iii|iv|v|vi|vii|viii|ix|x)$")) {
				kept.Add(t);
				continue;
			}
			int a = 0, u = 0, l = 0;
			foreach (char ch in t) {
				if (!char.IsLetter(ch)) continue;
				a++;
				if (char.IsUpper(ch)) u++;
				if (char.IsLower(ch)) l++;
			}
			double ur = a == 0 ? 0 : (u / (double)a);
			double lr = a == 0 ? 0 : (l / (double)a);
			bool titleLike = (a > 0 && ur >= 0.70) || (a == 0 && t.Length <= 3);
			if (kept.Count == 0) {
				if (titleLike) kept.Add(t);
				continue;
			}
			if (!titleLike && lr > 0.40) break;
			kept.Add(t);
		}

		string candidate = string.Join(' ', kept);
		candidate = Regex.Replace(candidate, @"\s+", " ").Trim();
		return candidate.Length >= 3 ? candidate : bestLine;
	}

	private static IEnumerable<string> ExtractOcrCandidateLines(params string[] ocrTexts) {
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string text in ocrTexts) {
			if (string.IsNullOrWhiteSpace(text)) continue;
			string[] lines = text
				.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length > 0)
				.ToArray();
			foreach (string line in lines) {
				string cleaned = Regex.Replace(line, @"[^A-Za-z0-9\s\-']", " ");
				cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
				if (cleaned.Length < 3) continue;
				if (seen.Add(cleaned)) yield return cleaned;
			}
		}
	}

	private static bool IsCategoryClassifierLine(string line) {
		if (string.IsNullOrWhiteSpace(line)) return false;
		string cleaned = Regex.Replace(line, @"[^A-Za-z0-9\s\-']", " ");
		cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
		if (cleaned.Length == 0) return false;
		string lower = cleaned.ToLowerInvariant();
		bool hasRarity = Regex.IsMatch(lower, @"\b(common|uncommon|rare|epic|legendary|mythic)\b");
		bool hasCategory = Regex.IsMatch(lower, @"\b(lmg|smg|ar|rifle|shotgun|pistol|sniper|ammo|magazine|armor|helmet|backpack|equipment|tool|resource|consumable|category)\b");
		int wordCount = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
		return hasRarity && (hasCategory || wordCount <= 3);
	}

	private static bool TryMatchItemFromOcr(string ocrTitle, string ocrText, out ArcRaidersData.ArcItem matchedItem, out float confidence, out bool isFuzzyMatch, out string matchedLine) {
		matchedItem = new ArcRaidersData.ArcItem();
		confidence = 0f;
		isFuzzyMatch = false;
		matchedLine = "";

		bool found = false;
		foreach (string candidate in ExtractOcrCandidateLines(ocrTitle, ocrText)) {
			if (IsCategoryClassifierLine(candidate)) continue;
			if (!TryMatchItemByName(candidate, out ArcRaidersData.ArcItem item, out float conf, out bool fuzzy)) continue;
			if (!found || conf > confidence) {
				matchedItem = item;
				confidence = conf;
				isFuzzyMatch = fuzzy;
				matchedLine = candidate;
				found = true;
			}
		}

		return found;
	}

	private static string ExtractTitleFromTitleRegion(string ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) return "";
		string[] lines = ocrText
			.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(l => l.Trim())
			.Where(l => l.Length > 0)
			.ToArray();

		foreach (string line in lines) {
			string cleaned = Regex.Replace(line, @"[^A-Za-z0-9\s\-']", " ");
			cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
			if (cleaned.Length < 4) continue;
			int letters = cleaned.Count(char.IsLetter);
			if (letters < 3) continue;
			int upper = cleaned.Count(char.IsUpper);
			double ratio = letters == 0 ? 0 : (double)upper / letters;
			// Title line is usually uppercase-heavy; description is lower-case heavy.
			if (ratio < 0.55) continue;
			if (IsCategoryClassifierLine(cleaned)) continue;
			return cleaned;
		}

		return "";
	}

	/// <summary>
	/// Try to match OCR text to an item in the database
	/// </summary>
	private static bool TryMatchItemByName(string ocrText, out ArcRaidersData.ArcItem matchedItem, out float confidence, out bool isFuzzyMatch) {
		matchedItem = new ArcRaidersData.ArcItem();
		confidence = 0;
		isFuzzyMatch = false;
		
		if (string.IsNullOrWhiteSpace(ocrText)) return false;
		if (IsCategoryClassifierLine(ocrText)) return false;
		
		string normalizedOcr = NormalizeItemName(ocrText);
		Logger.LogDebug($"TryMatchItemByName: normalized OCR = '{normalizedOcr}'");
		string? ocrRoman = ExtractRomanSuffix(normalizedOcr);
		
		var items = ArcRaidersData.GetItems();
		
		// Try exact match first
		foreach (var item in items) {
			string normalizedName = NormalizeItemName(item.Name);
			if (ocrRoman != null) {
				string? itemRoman = ExtractRomanSuffix(normalizedName);
				if (itemRoman != null && !string.Equals(itemRoman, ocrRoman, StringComparison.OrdinalIgnoreCase)) continue;
			}
			if (normalizedOcr.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)) {
				matchedItem = item;
				confidence = 1.0f;
				isFuzzyMatch = false;
				return true;
			}
		}
		
		// Try contains match (OCR might have extra text or be a substring)
		foreach (var item in items) {
			string normalizedName = NormalizeItemName(item.Name);
			if (ocrRoman != null) {
				string? itemRoman = ExtractRomanSuffix(normalizedName);
				if (itemRoman != null && !string.Equals(itemRoman, ocrRoman, StringComparison.OrdinalIgnoreCase)) continue;
			}
			if (normalizedOcr.Contains(normalizedName, StringComparison.OrdinalIgnoreCase) ||
			    normalizedName.Contains(normalizedOcr, StringComparison.OrdinalIgnoreCase)) {
				matchedItem = item;
				confidence = 0.9f;
				isFuzzyMatch = false;
				return true;
			}
		}
		
		// Try word-based matching (find items where OCR text contains item name words)
		foreach (var item in items) {
			string[] itemWords = item.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			// For items with multiple words (like "Burletta I"), check if all words appear in OCR
			if (itemWords.Length >= 1) {
				bool allWordsFound = true;
				foreach (string word in itemWords) {
					if (word.Length < 2) continue;  // Skip very short words like "I"
					if (!normalizedOcr.Contains(word, StringComparison.OrdinalIgnoreCase)) {
						allWordsFound = false;
						break;
					}
				}
				if (allWordsFound && itemWords.Any(w => w.Length >= 3)) {
					if (ocrRoman != null) {
						string? itemRoman = ExtractRomanSuffix(NormalizeItemName(item.Name));
						if (itemRoman != null && !string.Equals(itemRoman, ocrRoman, StringComparison.OrdinalIgnoreCase)) continue;
					}
					matchedItem = item;
					confidence = 0.85f;
					isFuzzyMatch = false;
					return true;
				}
			}
		}
		
		// Try fuzzy matching (Levenshtein distance)
		ArcRaidersData.ArcItem? bestMatch = null;
		int bestDistance = int.MaxValue;
		int ocrLength = normalizedOcr.Length;
		
		foreach (var item in items) {
			string normalizedName = NormalizeItemName(item.Name);
			if (ocrRoman != null) {
				string? itemRoman = ExtractRomanSuffix(normalizedName);
				if (itemRoman != null && !string.Equals(itemRoman, ocrRoman, StringComparison.OrdinalIgnoreCase)) continue;
			}
			int distance = LevenshteinDistance(normalizedOcr, normalizedName);
			
			// Allow up to ~40% character difference for fuzzy matching
			int maxAllowedDistance = Math.Max(4, Math.Min(ocrLength, normalizedName.Length) * 2 / 5);
			
			if (distance < bestDistance && distance <= maxAllowedDistance) {
				bestDistance = distance;
				bestMatch = item;
			}
		}
		
		if (bestMatch != null) {
			matchedItem = bestMatch;
			// Calculate confidence based on edit distance
			int maxLen = Math.Max(normalizedOcr.Length, matchedItem.Name.Length);
			confidence = Math.Max(0, 1.0f - (bestDistance / (float)maxLen));
			isFuzzyMatch = true;
			return confidence >= 0.5f;  // Lower threshold for fuzzy matches
		}
		
		return false;
	}

	private static string? ExtractRomanSuffix(string normalizedName) {
		if (string.IsNullOrWhiteSpace(normalizedName)) return null;
		Match m = Regex.Match(normalizedName, @"\b(i|ii|iii|iv|v|vi|vii|viii|ix|x)\b\s*$", RegexOptions.IgnoreCase);
		return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
	}

	/// <summary>
	/// Normalize an item name for comparison
	/// </summary>
	private static string NormalizeItemName(string name) {
		if (string.IsNullOrWhiteSpace(name)) return "";
		
		// Remove extra whitespace and convert to lowercase
		name = Regex.Replace(name, @"\s+", " ").Trim().ToLowerInvariant();
		
		// Remove common OCR artifacts
		name = name.Replace("|", "i").Replace("1", "i").Replace("0", "o");
		// Strip odd punctuation that tends to show up in OCR output.
		name = Regex.Replace(name, @"[^a-z0-9\s\-']", " ");
		name = Regex.Replace(name, @"\s+", " ").Trim();
		
		return name;
	}

	/// <summary>
	/// Calculate Levenshtein (edit) distance between two strings
	/// </summary>
	private static int LevenshteinDistance(string s1, string s2) {
		int[,] d = new int[s1.Length + 1, s2.Length + 1];
		
		for (int i = 0; i <= s1.Length; i++) d[i, 0] = i;
		for (int j = 0; j <= s2.Length; j++) d[0, j] = j;
		
		for (int i = 1; i <= s1.Length; i++) {
			for (int j = 1; j <= s2.Length; j++) {
				int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
				d[i, j] = Math.Min(
					Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
					d[i - 1, j - 1] + cost);
			}
		}
		
		return d[s1.Length, s2.Length];
	}

	// Returns the ruff screenshot
	private Bitmap GetScreenshot(Vector2 vector2, Size size) {
		Bitmap bmp = new(size.Width, size.Height, PixelFormat.Format24bppRgb);

		try {
			using Graphics gfx = Graphics.FromImage(bmp);
			gfx.CopyFromScreen(vector2.X, vector2.Y, 0, 0, size, CopyPixelOperation.SourceCopy);
		} catch (Exception e) {
			Logger.LogWarning("Unable to capture screenshot", e);
		}

		return bmp;
	}

	private void RefreshOverlay(object? o = null) {
		OnPropertyChanged();
	}

	private void EnqueueFallbackScan(Vector2 position, string reason) {
		var fallbackItem = new ArcRaidersData.ArcItem {
			Name = $"Scan failed: {reason}",
			ShortName = "Unknown"
		};
		ItemScans.Enqueue(new DefaultItemScan(fallbackItem, position, RatConfig.ToolTip.Duration));
		RefreshOverlay();
	}

	private bool TryMatchSelectedIconHash(Vector2 mousePosition, string captureLabel, out ArcRaidersData.ArcItem matchedItem, out float confidence, out string matchedIconPath) {
		matchedItem = new ArcRaidersData.ArcItem();
		matchedIconPath = "";
		confidence = 0f;

		int size = Math.Min(RatConfig.IconScan.SelectionScanSize, (int)(RatConfig.GameScale * 260));
		Vector2 topLeft = new(mousePosition.X - size / 2, mousePosition.Y - size / 2);
		using Bitmap screenshot = GetScreenshot(topLeft, new Size(size, size));

		if (!TryFindHighlightBounds(screenshot, new System.Drawing.Point(size / 2, size / 2), out System.Drawing.Rectangle bounds)) {
			Logger.LogDebug("TryMatchSelectedIconHash: no highlight bounds found");
			return false;
		}
		if (bounds.Width < 40 || bounds.Height < 40) {
			Logger.LogDebug($"TryMatchSelectedIconHash: highlight bounds too small {bounds}");
			return false;
		}

		System.Drawing.Rectangle inner = bounds;
		inner.Inflate(-8, -8);
		inner = System.Drawing.Rectangle.Intersect(inner, new System.Drawing.Rectangle(0, 0, screenshot.Width, screenshot.Height));
		if (inner.Width <= 0 || inner.Height <= 0) {
			Logger.LogDebug("TryMatchSelectedIconHash: inner bounds invalid");
			return false;
		}

		using Bitmap iconCrop = screenshot.Clone(inner, PixelFormat.Format24bppRgb);
		EnsureIconHashes();
		if (_iconHashes == null || _iconHashes.Count == 0) {
			Logger.LogDebug("TryMatchSelectedIconHash: no icon hashes loaded");
			return false;
		}

		ulong hash0 = ComputeIconHash(iconCrop);
		ulong hash90;
		ulong hash180;
		ulong hash270;
		using (Bitmap r90 = (Bitmap)iconCrop.Clone()) {
			r90.RotateFlip(RotateFlipType.Rotate90FlipNone);
			hash90 = ComputeIconHash(r90);
		}
		using (Bitmap r180 = (Bitmap)iconCrop.Clone()) {
			r180.RotateFlip(RotateFlipType.Rotate180FlipNone);
			hash180 = ComputeIconHash(r180);
		}
		using (Bitmap r270 = (Bitmap)iconCrop.Clone()) {
			r270.RotateFlip(RotateFlipType.Rotate270FlipNone);
			hash270 = ComputeIconHash(r270);
		}

		var rotations = new (string Name, ulong Hash)[] {
			("0", hash0),
			("90", hash90),
			("180", hash180),
			("270", hash270)
		};

		string? bestId = null;
		int bestDistance = int.MaxValue;
		float bestScore = 0f;
		string bestRotation = "0";
		var topMatches = new List<(string id, int distance, float score, string rotation)>();

		foreach (var entry in _iconHashes) {
			foreach (var rot in rotations) {
				int distance = HammingDistance(rot.Hash, entry.Value);
				float score = 1.0f - (distance / 64f);
				if (topMatches.Count < 5 || distance < topMatches[topMatches.Count - 1].distance) {
					topMatches.Add((entry.Key, distance, score, rot.Name));
					topMatches.Sort((a, b) => a.distance.CompareTo(b.distance));
					if (topMatches.Count > 5) topMatches.RemoveAt(5);
				}
				if (distance < bestDistance) {
					bestDistance = distance;
					bestId = entry.Key;
					bestScore = score;
					bestRotation = rot.Name;
				}
			}
		}

		Logger.LogDebug($"TryMatchSelectedIconHash: hashes=0x{hash0:X16},0x{hash90:X16},0x{hash180:X16},0x{hash270:X16}");
		Logger.LogDebug("TryMatchSelectedIconHash: top 5 matches:");
		foreach (var match in topMatches) {
			var item = ArcRaidersData.GetItemById(match.id);
			Logger.LogDebug($"  - {match.id} ({item?.Name ?? "unknown"}): distance={match.distance}, score={match.score:0.00}, rot={match.rotation}");
		}

		if (RatConfig.LogDebug) {
			try {
				string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
				screenshot.Save(Path.Combine(RatConfig.Paths.Debug, $"selected_{timestamp}_{captureLabel}_scan.png"), System.Drawing.Imaging.ImageFormat.Png);
				iconCrop.Save(Path.Combine(RatConfig.Paths.Debug, $"selected_{timestamp}_{captureLabel}_icon.png"), System.Drawing.Imaging.ImageFormat.Png);
			} catch { /* Ignore debug save errors */ }
		}

		if (bestId == null) return false;
		Logger.LogDebug($"TryMatchSelectedIconHash: bestMatch='{bestId}', distance={bestDistance}, score={bestScore:0.00}, rot={bestRotation}");
		if (bestDistance > 12 || bestScore < 0.80f) {
			Logger.LogDebug($"TryMatchSelectedIconHash: rejecting match (distance={bestDistance}, score={bestScore:0.00})");
			return false;
		}

		matchedItem = ArcRaidersData.GetItemById(bestId) ?? new ArcRaidersData.ArcItem { Id = bestId, Name = bestId, ShortName = bestId };
		matchedIconPath = Path.Combine(RatConfig.Paths.StaticIcon, $"{bestId}.png");
		confidence = Math.Max(0.3f, bestScore);
		return true;
	}

	private bool TryMatchSelectedIcon(Vector2 mousePosition, out ArcRaidersData.ArcItem matchedItem, out string matchedIconPath) {
		matchedItem = new ArcRaidersData.ArcItem();
		matchedIconPath = "";

		int size = RatConfig.IconScan.SelectionScanSize;
		Vector2 topLeft = new(mousePosition.X - size / 2, mousePosition.Y - size / 2);
		using Bitmap screenshot = GetScreenshot(topLeft, new Size(size, size));

		if (!TryFindHighlightBounds(screenshot, new System.Drawing.Point(size / 2, size / 2), out System.Drawing.Rectangle bounds)) return false;
		if (bounds.Width < 40 || bounds.Height < 40) return false;

		System.Drawing.Rectangle inner = bounds;
		inner.Inflate(-8, -8);
		inner = System.Drawing.Rectangle.Intersect(inner, new System.Drawing.Rectangle(0, 0, screenshot.Width, screenshot.Height));
		if (inner.Width <= 0 || inner.Height <= 0) return false;

		using Bitmap iconCrop = screenshot.Clone(inner, PixelFormat.Format24bppRgb);
		EnsureIconHashes();

		ulong hash = ComputeIconHash(iconCrop);
		string? bestId = null;
		int bestDistance = int.MaxValue;

		foreach (var entry in _iconHashes!) {
			int distance = HammingDistance(hash, entry.Value);
			if (distance < bestDistance) {
				bestDistance = distance;
				bestId = entry.Key;
			}
		}

		if (bestId == null || bestDistance > 14) return false;
		matchedItem = ArcRaidersData.GetItemById(bestId) ?? new ArcRaidersData.ArcItem { Id = bestId, Name = bestId, ShortName = bestId };
		matchedIconPath = Path.Combine(RatConfig.Paths.StaticIcon, $"{bestId}.png");
		return true;
	}

	private void EnsureIconHashes() {
		lock (_iconHashLock) {
			if (_iconHashes != null) return;
			_iconHashes = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
			foreach (string file in Directory.EnumerateFiles(RatConfig.Paths.StaticIcon, "*.png")) {
				string id = Path.GetFileNameWithoutExtension(file);
				try {
					using Bitmap bmp = new(file);
					_iconHashes[id] = ComputeIconHash(bmp);
				} catch (Exception e) {
					Logger.LogDebug($"Icon hash failed for '{id}': {e.Message}");
				}
			}
		}
	}

	private static ulong ComputeDHash(Bitmap bitmap) {
		using Bitmap resized = new(9, 8, PixelFormat.Format24bppRgb);
		using Graphics gfx = Graphics.FromImage(resized);
		gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
		gfx.DrawImage(bitmap, 0, 0, 9, 8);

		ulong hash = 0;
		int bit = 0;
		for (int y = 0; y < 8; y++) {
			for (int x = 0; x < 8; x++) {
				System.Drawing.Color left = resized.GetPixel(x, y);
				System.Drawing.Color right = resized.GetPixel(x + 1, y);
				int leftLum = (left.R * 299 + left.G * 587 + left.B * 114) / 1000;
				int rightLum = (right.R * 299 + right.G * 587 + right.B * 114) / 1000;
				if (leftLum < rightLum) hash |= 1UL << bit;
				bit++;
			}
		}
		return hash;
	}

	private static int HammingDistance(ulong a, ulong b) {
		ulong x = a ^ b;
		int count = 0;
		while (x != 0) {
			x &= x - 1;
			count++;
		}
		return count;
	}

	private static ulong ComputeIconHash(Bitmap bitmap) {
		using Bitmap edge = CreateEdgeMap(bitmap, IconHashBackground);
		return ComputeDHash(edge);
	}

	private static Bitmap CreateEdgeMap(Bitmap src, System.Drawing.Color background) {
		using Bitmap resized = new(64, 64, PixelFormat.Format24bppRgb);
		using (Graphics gfx = Graphics.FromImage(resized)) {
			gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
			gfx.Clear(background);
			gfx.DrawImage(src, 0, 0, 64, 64);
		}

		Bitmap edge = new(64, 64, PixelFormat.Format24bppRgb);
		for (int y = 1; y < 63; y++) {
			for (int x = 1; x < 63; x++) {
				int gx = 0;
				int gy = 0;
				for (int ky = -1; ky <= 1; ky++) {
					for (int kx = -1; kx <= 1; kx++) {
						System.Drawing.Color c = resized.GetPixel(x + kx, y + ky);
						int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
						int sx = (kx == -1 ? -1 : (kx == 1 ? 1 : 0)) * (ky == 0 ? 2 : 1);
						int sy = (ky == -1 ? -1 : (ky == 1 ? 1 : 0)) * (kx == 0 ? 2 : 1);
						gx += lum * sx;
						gy += lum * sy;
					}
				}
				int mag = Math.Min(255, (Math.Abs(gx) + Math.Abs(gy)) / 4);
				edge.SetPixel(x, y, System.Drawing.Color.FromArgb(mag, mag, mag));
			}
		}
		return edge;
	}

	private static bool TryFindHighlightBounds(Bitmap bitmap, System.Drawing.Point center, out System.Drawing.Rectangle bounds) {
		bounds = System.Drawing.Rectangle.Empty;
		int minX = bitmap.Width, minY = bitmap.Height, maxX = 0, maxY = 0;
		bool found = false;

		for (int y = 0; y < bitmap.Height; y++) {
			for (int x = 0; x < bitmap.Width; x++) {
				System.Drawing.Color c = bitmap.GetPixel(x, y);
				if (!IsHighlightColor(c)) continue;
				found = true;
				if (x < minX) minX = x;
				if (y < minY) minY = y;
				if (x > maxX) maxX = x;
				if (y > maxY) maxY = y;
			}
		}

		if (!found) {
			return TryFindBrightBounds(bitmap, center, out bounds);
		}

		bounds = System.Drawing.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
		float ratio = bounds.Width / (float)bounds.Height;
		if (ratio < 0.7f || ratio > 1.3f) return false;

		System.Drawing.Point boxCenter = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
		int dx = boxCenter.X - center.X;
		int dy = boxCenter.Y - center.Y;
		if (dx * dx + dy * dy > (bitmap.Width * bitmap.Width) / 4) return false;

		return true;
	}

	private static bool TryFindBrightBounds(Bitmap bitmap, System.Drawing.Point center, out System.Drawing.Rectangle bounds) {
		bounds = System.Drawing.Rectangle.Empty;
		int minX = bitmap.Width, minY = bitmap.Height, maxX = 0, maxY = 0;
		bool found = false;

		for (int y = 0; y < bitmap.Height; y++) {
			for (int x = 0; x < bitmap.Width; x++) {
				System.Drawing.Color c = bitmap.GetPixel(x, y);
				int max = Math.Max(c.R, Math.Max(c.G, c.B));
				int min = Math.Min(c.R, Math.Min(c.G, c.B));
				int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
				bool colorfulBright = luma > 170 && (max - min) > 25;
				bool veryBright = luma > 220;
				if (!colorfulBright && !veryBright) continue;
				found = true;
				if (x < minX) minX = x;
				if (y < minY) minY = y;
				if (x > maxX) maxX = x;
				if (y > maxY) maxY = y;
			}
		}

		if (!found) return false;
		bounds = System.Drawing.Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
		float ratio = bounds.Width / (float)bounds.Height;
		if (ratio < 0.7f || ratio > 1.3f) return false;

		System.Drawing.Point boxCenter = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
		int dx = boxCenter.X - center.X;
		int dy = boxCenter.Y - center.Y;
		if (dx * dx + dy * dy > (bitmap.Width * bitmap.Width) / 4) return false;

		return true;
	}

	private static bool IsHighlightColor(System.Drawing.Color c) {
		int max = Math.Max(c.R, Math.Max(c.G, c.B));
		int min = Math.Min(c.R, Math.Min(c.G, c.B));
		int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
		bool brightWhite = luma > 220;
		bool brightColor = luma > 170 && (max - min) > 35;
		bool bluePurple = c.B > 160 && c.R > 100 && c.G > 80 && (c.B - c.G) > 20;
		return brightWhite || brightColor || bluePurple;
	}

	protected virtual void OnPropertyChanged(string? propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}
