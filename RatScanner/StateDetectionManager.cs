using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using Tesseract;

namespace RatScanner;

/// <summary>
/// Manages periodic screen capture for detecting quest menu, workbench, and other UI states
/// </summary>
public class StateDetectionManager : IDisposable {
	private readonly Timer _captureTimer;
	private bool _isEnabled = false;
	private DateTime _lastCapture = DateTime.MinValue;
	private const int CaptureIntervalMs = 5000; // Capture every 5 seconds when enabled
	private static readonly HttpClient HttpClient = new();
	private static bool _ocrInitialized;
	private DateTime _lastQuestExtractUtc = DateTime.MinValue;
	private DateTime _lastWorkbenchExtractUtc = DateTime.MinValue;
	private DateTime _lastMapExtractUtc = DateTime.MinValue;
	private const int ExtractCooldownMs = 8000;
	
	public event Action<DetectedState>? StateDetected;
	
	public enum DetectedState {
		QuestMenu,
		WorkbenchMenu,
		BlueprintMenu,
		TrackedResourcesMenu,
		MapView,
		InRaid,
		Unknown
	}
	
	public StateDetectionManager() {
		_captureTimer = new Timer(CaptureIntervalMs);
		_captureTimer.Elapsed += OnCaptureTimerElapsed;
		_captureTimer.AutoReset = true;
	}
	
	public void Start() {
		if (_isEnabled) return;
		_isEnabled = true;
		_captureTimer.Start();
		Logger.LogInfo("State detection started");
	}
	
	public void Stop() {
		if (!_isEnabled) return;
		_isEnabled = false;
		_captureTimer.Stop();
		Logger.LogInfo("State detection stopped");
	}

	public async Task<bool> RunManualMapCalibration() {
		try {
			await Task.Delay(2000); // Give user time to open map if they clicked "Calibrate"
            // Or if we prompt them "Open map and press OK", the delay might be shorter or 0.
            // But let's assume immediate action after UI interaction.

			var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
			using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
			
			// Check for map view
			
			// Map specific region checks
			// 1. Right side panel (where "Dam Battlegrounds" text was spotted)
			// Region: Top 40% of right 30% of screen
			var rightPanelRegion = new Rectangle(
				(int)(bounds.Width * 0.7), 
				0, 
				(int)(bounds.Width * 0.3), 
				(int)(bounds.Height * 0.4));
			
			string rightPanelText = ReadOcrInRegion(bitmap, rightPanelRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
			Logger.LogInfo($"Calibration OCR (Right Panel): '{rightPanelText.Replace("\n", " ")}'");
			
			var map = MatchMapFromText(rightPanelText);
			if (map != null) {
				MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id);
				Logger.LogInfo($"Manual calibration success (Right Panel): {map.GetName()}");
				return true;
			}
			
			// 2. Top center (fallback)
			var topRegion = GetTopRegion(bitmap, 0.2);
			string topText = ReadOcrInRegion(bitmap, topRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
			Logger.LogInfo($"Calibration OCR (Top Center): '{topText.Replace("\n", " ")}'");
			
			map = MatchMapFromText(topText);
			if (map != null) {
				MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id);
				Logger.LogInfo($"Manual calibration success (Top Center): {map.GetName()}");
                return true;
			}
			
			Logger.LogWarning("Manual calibration: Could not detect map name in looked regions.");
            return false;
		} catch (Exception ex) {
			Logger.LogError("Manual calibration failed", ex);
            return false;
		}
	}
	
	private async void OnCaptureTimerElapsed(object? sender, ElapsedEventArgs e) {
		if (!_isEnabled) return;
		
		try {
			_lastCapture = DateTime.UtcNow;
			await Task.Run(() => CaptureAndAnalyzeScreen());
		} catch (Exception ex) {
			Logger.LogDebug($"State detection error: {ex.Message}");
		}
	}
	
	private void CaptureAndAnalyzeScreen() {
		try {
			// Capture full screen
			var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
			using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
			
			// Analyze the capture to detect UI state
			var state = AnalyzeScreenCapture(bitmap);
			if (state != DetectedState.Unknown) {
				StateDetected?.Invoke(state);
				ProcessDetectedState(state, bitmap);
			}
		} catch (Exception ex) {
			Logger.LogDebug($"Screen capture failed: {ex.Message}");
		}
	}
	
	private DetectedState AnalyzeScreenCapture(Bitmap bitmap) {
		// Simple heuristic detection based on UI patterns
		// In a real implementation, this would use more sophisticated OCR/pattern matching
		
		// Check for quest menu indicators (e.g., "QUESTS" text at top, quest list layout)
		if (ContainsQuestMenuIndicators(bitmap)) {
			return DetectedState.QuestMenu;
		}
		
		// Check for workbench menu (crafting interface patterns)
		if (ContainsWorkbenchMenuIndicators(bitmap)) {
			return DetectedState.WorkbenchMenu;
		}
		
		// Check for blueprint/project menu
		if (ContainsBlueprintMenuIndicators(bitmap)) {
			return DetectedState.BlueprintMenu;
		}
		
		// Check for tracked resources menu
		if (ContainsTrackedResourcesIndicators(bitmap)) {
			return DetectedState.TrackedResourcesMenu;
		}
		
		// Check for map view
		if (ContainsMapViewIndicators(bitmap)) {
			return DetectedState.MapView;
		}
		
		return DetectedState.Unknown;
	}
	
	private bool ContainsQuestMenuIndicators(Bitmap bitmap) {
		// TODO: Implement OCR-based detection for quest menu
		// Look for: "QUESTS", "ACTIVE", "COMPLETED" text
		// Look for quest list layout patterns
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.25), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		return text.Contains("QUEST", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase);
	}
	
	private bool ContainsWorkbenchMenuIndicators(Bitmap bitmap) {
		// TODO: Implement detection for workbench interface
		// Look for: workbench name, level indicator, "CRAFT" button
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.35), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		return text.Contains("WORKBENCH", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("CRAFT", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("UPGRADE", StringComparison.OrdinalIgnoreCase);
	}
	
	private bool ContainsBlueprintMenuIndicators(Bitmap bitmap) {
		// TODO: Implement detection for blueprint/project menu
		// Look for: "BLUEPRINTS", "PROJECTS", unlock indicators
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.35), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		return text.Contains("BLUEPRINT", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("PROJECT", StringComparison.OrdinalIgnoreCase);
	}
	
	private bool ContainsTrackedResourcesIndicators(Bitmap bitmap) {
		// TODO: Implement detection for tracked resources display
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.35), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		return text.Contains("TRACKED", StringComparison.OrdinalIgnoreCase)
		       || text.Contains("RESOURCES", StringComparison.OrdinalIgnoreCase);
	}
	
	private bool ContainsMapViewIndicators(Bitmap bitmap) {
		// TODO: Implement detection for map screen
		// Look for: map graphics, location markers
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.25), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		if (text.Contains("MAP", StringComparison.OrdinalIgnoreCase)) return true;
		var mapMatch = MatchMapFromText(text);
		return mapMatch != null;
	}
	
	private void ProcessDetectedState(DetectedState state, Bitmap bitmap) {
		Logger.LogDebug($"Detected state: {state}");
		
        // Clone bitmap for async processing to avoid object disposed exception
        // when the calling method disposes the original bitmap.
        Bitmap clone = new Bitmap(bitmap);

		_ = Task.Run(() => {
            try {
                switch (state) {
                    case DetectedState.QuestMenu:
                        ExtractQuestInfo(clone);
                        break;
                    case DetectedState.WorkbenchMenu:
                        ExtractWorkbenchInfo(clone);
                        break;
                    case DetectedState.BlueprintMenu:
                        ExtractBlueprintInfo(clone);
                        break;
                    case DetectedState.TrackedResourcesMenu:
                        ExtractTrackedResourcesInfo(clone);
                        break;
                    case DetectedState.MapView:
                        ExtractMapInfo(clone);
                        break;
                }
            } catch (Exception ex) {
                Logger.LogError($"Error processing state {state}: {ex.Message}", ex);
            } finally {
                clone.Dispose();
            }
        });
	}
	
	private void ExtractQuestInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastQuestExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastQuestExtractUtc = DateTime.UtcNow;

			var questRegion = GetLeftRegion(bitmap, 0.6, 0.8);
			string ocrText = ReadOcrInRegion(bitmap, questRegion, PageSegMode.Auto, null);
			if (string.IsNullOrWhiteSpace(ocrText)) return;

			string normalized = NormalizeText(ocrText);
			var allQuests = ArcRaidersData.GetQuests();
			var matches = new List<RaidTheoryDataSource.RaidTheoryQuest>();
			foreach (var quest in allQuests) {
				string name = quest.GetName();
				if (string.IsNullOrWhiteSpace(name)) continue;
				string normName = NormalizeText(name);
				if (normName.Length < 4) continue;
				if (normalized.Contains(normName)) {
					matches.Add(quest);
				}
			}

			if (matches.Count > 0) {
				PlayerStateManager.SetActiveQuests(matches.Select(q => q.Id));
				Logger.LogInfo($"Detected {matches.Count} active quests from screen");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Quest extraction failed: {ex.Message}");
		}
	}
	
	private void ExtractWorkbenchInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastWorkbenchExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastWorkbenchExtractUtc = DateTime.UtcNow;

			var lowerRegion = GetBottomRegion(bitmap, 0.5);
			string ocrText = ReadOcrInRegion(bitmap, lowerRegion, PageSegMode.Auto, "IVX0123456789");
			if (string.IsNullOrWhiteSpace(ocrText)) return;

			var levels = ExtractWorkbenchLevels(ocrText).ToList();
			if (levels.Count == 0) return;

			var modules = ArcRaidersData.GetHideoutModules()
				.OrderBy(m => m.GetName())
				.ToList();
			if (modules.Count == 0) return;

			int assignCount = Math.Min(levels.Count, modules.Count);
			var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < assignCount; i++) {
				map[modules[i].Id] = levels[i];
			}

			if (map.Count > 0) {
				PlayerStateManager.SetWorkbenchLevels(map);
				Logger.LogInfo($"Detected {map.Count} workbench levels from screen");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Workbench extraction failed: {ex.Message}");
		}
	}
	
	private void ExtractBlueprintInfo(Bitmap bitmap) {
		try {
			// TODO: Use OCR to extract learned blueprints
			// Look for checkmarks or "LEARNED" indicators next to blueprint names
			
			// Example: If we detect learned blueprints
			// PlayerStateManager.LearnBlueprint("blueprint_id");
			
			Logger.LogDebug("Blueprint extraction not yet implemented");
		} catch (Exception ex) {
			Logger.LogWarning($"Blueprint extraction failed: {ex.Message}");
		}
	}
	
	private void ExtractTrackedResourcesInfo(Bitmap bitmap) {
		try {
			// TODO: Use OCR to extract tracked resource list
			// Update PlayerStateManager with tracked items
			
			Logger.LogDebug("Tracked resources extraction not yet implemented");
		} catch (Exception ex) {
			Logger.LogWarning($"Tracked resources extraction failed: {ex.Message}");
		}
	}

	private void ExtractMapInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastMapExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastMapExtractUtc = DateTime.UtcNow;

			var topRegion = GetTopRegion(bitmap, 0.2);
			string ocrText = ReadOcrInRegion(bitmap, topRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
			var map = MatchMapFromText(ocrText);
			if (map == null) return;

			MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id);
			Logger.LogInfo($"Map detected: {map.GetName()} (position set to center)");
		} catch (Exception ex) {
			Logger.LogWarning($"Map extraction failed: {ex.Message}");
		}
	}

	private static string NormalizeText(string value) {
		value = value.ToLowerInvariant();
		value = Regex.Replace(value, @"[^a-z0-9 ]", " ");
		value = Regex.Replace(value, @"\s+", " ").Trim();
		return value;
	}

	private static string ReadOcrInRegion(Bitmap bitmap, Rectangle region, PageSegMode mode, string? whitelist) {
		if (!_ocrInitialized) {
			EnsureTrainedData();
		}
		region = Rectangle.Intersect(region, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
		if (region.Width < 10 || region.Height < 10) return "";

		using Bitmap crop = bitmap.Clone(region, PixelFormat.Format24bppRgb);
		using Bitmap pre = PreprocessForOcr(crop);
		return PerformOcr(pre, mode, whitelist);
	}

	private static Rectangle GetTopRegion(Bitmap bitmap, double heightRatio) {
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(0, 0, bitmap.Width, height);
	}

	private static Rectangle GetBottomRegion(Bitmap bitmap, double heightRatio) {
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(0, bitmap.Height - height, bitmap.Width, height);
	}

	private static Rectangle GetLeftRegion(Bitmap bitmap, double widthRatio, double heightRatio) {
		int width = Math.Max(1, (int)(bitmap.Width * widthRatio));
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(0, 0, width, height);
	}

	private static IEnumerable<int> ExtractWorkbenchLevels(string ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) yield break;
		string normalized = ocrText.Replace("\n", " ");
		foreach (Match match in Regex.Matches(normalized, @"\b(IV|III|II|I|V|VI|VII|VIII|IX|X|\d+)\b", RegexOptions.IgnoreCase)) {
			string token = match.Value.ToUpperInvariant();
			if (int.TryParse(token, out int num)) {
				yield return Math.Clamp(num, 0, 10);
				continue;
			}
			yield return token switch {
				"I" => 1,
				"II" => 2,
				"III" => 3,
				"IV" => 4,
				"V" => 5,
				"VI" => 6,
				"VII" => 7,
				"VIII" => 8,
				"IX" => 9,
				"X" => 10,
				_ => 0
			};
		}
	}

	private static RaidTheoryDataSource.RaidTheoryMap? MatchMapFromText(string ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) return null;
		string normalized = NormalizeText(ocrText);
		var maps = ArcRaidersData.GetMaps();
		
		// 1. Exact string containment (Best case)
		foreach (var map in maps) {
			string name = map.GetName();
			if (string.IsNullOrWhiteSpace(name)) continue;
			string normName = NormalizeText(name);
			if (normName.Length < 3) continue;
			if (normalized.Contains(normName)) return map;
		}

		// 2. Fuzzy matching (Fallback)
		// Only attempt if the text length is somewhat similar to the map name, 
		// verifying that we captured just the map title or close to it.
		foreach (var map in maps) {
			string name = map.GetName();
			string normName = NormalizeText(name);
			if (normName.Length < 3) continue;
			
			// Compute distance
			// If the captured text is significantly longer than the map name (e.g. captures "Location: Dam Battlegrounds Sector 5"),
			// Levenshtein on the whole string will fail. 
			// But for calibration we often capture a tighter region or just the title.
			
			// We only fuzzy match if lengths are within a reasonable margin,
			// effectively checking if the *entire* OCR text is the map name.
			if (Math.Abs(normalized.Length - normName.Length) <= 5) {
				int dist = normalized.LevenshteinDistance(normName);
				// Allow up to 20% errors or 3 chars
				int threshold = Math.Max(3, normName.Length / 5);
				
				if (dist <= threshold) {
					Logger.LogInfo($"Fuzzy map match: '{normalized}' ~= '{normName}' (Dist: {dist})");
					return map;
				}
			}
		}

		return null;
	}

	private static void EnsureTrainedData() {
		if (_ocrInitialized) return;
		_ocrInitialized = true;
		try {
			string engTrainedDataPath = Path.Combine(RatConfig.Paths.TrainedData, "eng.traineddata");
			if (File.Exists(engTrainedDataPath)) return;
			string url = "https://github.com/tesseract-ocr/tessdata_best/raw/main/eng.traineddata";
			Logger.LogInfo("Downloading Tesseract English traineddata for OCR...");
			byte[] data = HttpClient.GetByteArrayAsync(url).GetAwaiter().GetResult();
			Directory.CreateDirectory(RatConfig.Paths.TrainedData);
			File.WriteAllBytes(engTrainedDataPath, data);
			Logger.LogInfo("Tesseract traineddata downloaded successfully.");
		} catch (Exception e) {
			Logger.LogWarning($"Failed to download traineddata: {e.Message}. State OCR will not work.", e);
		}
	}

	private static Bitmap PreprocessForOcr(Bitmap source) {
		int scale = 2;
		Bitmap result = new(source.Width * scale, source.Height * scale, PixelFormat.Format24bppRgb);
		using (Graphics gfx = Graphics.FromImage(result)) {
			gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
			gfx.DrawImage(source, 0, 0, result.Width, result.Height);
		}

		BitmapData bmpData = result.LockBits(
			new Rectangle(0, 0, result.Width, result.Height),
			ImageLockMode.ReadWrite,
			PixelFormat.Format24bppRgb);

		try {
			int stride = bmpData.Stride;
			int bytesPerPixel = 3;
			int byteCount = stride * result.Height;
			byte[] pixels = new byte[byteCount];
			System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, pixels, 0, byteCount);

			for (int y = 0; y < result.Height; y++) {
				for (int x = 0; x < result.Width; x++) {
					int offset = y * stride + x * bytesPerPixel;
					byte b = pixels[offset];
					byte g = pixels[offset + 1];
					byte r = pixels[offset + 2];
					int gray = (r * 299 + g * 587 + b * 114) / 1000;
					byte newValue = gray < 160 ? (byte)0 : (byte)255;
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

	private static string PerformOcr(Bitmap image, PageSegMode pageSegMode, string? whitelist) {
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
			text = text.Replace("\r\n", "\n").Replace("\r", "\n");
			text = Regex.Replace(text, @"[\t\f\v]+", " ");
			text = Regex.Replace(text, @"[ ]{2,}", " ");
			text = Regex.Replace(text, @"\n{3,}", "\n\n");
			return text.Trim();
		} catch (Exception e) {
			Logger.LogWarning($"OCR failed: {e.Message}", e);
			return "";
		}
	}
	
	public void Dispose() {
		_captureTimer?.Dispose();
		GC.SuppressFinalize(this);
	}
}
