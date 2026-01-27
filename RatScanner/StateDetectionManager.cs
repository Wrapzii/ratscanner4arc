using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;
using Tesseract;

namespace RatScanner;

/// <summary>
/// Manages periodic screen capture for detecting quest menu, workbench, and other UI states
/// </summary>
public class StateDetectionManager : IDisposable {
	private readonly Timer _captureTimer;
	private bool _isEnabled = false;
	private DateTime _lastCapture = DateTime.MinValue;
	private const int CaptureIntervalMs = 750; // Capture every 0.75 seconds when enabled
	private static readonly HttpClient HttpClient = new();
	private static bool _ocrInitialized;
	private const bool EnableLegacyQuestMenuDetection = false;
	private static readonly HashSet<string> OcrMapStopWords = new(StringComparer.OrdinalIgnoreCase) {
		"inventory", "crafting", "map", "logbook", "system", "fps",
		"gpu", "gpuu", "ft", "fl", "ff", "f", "m"
	};
	private DateTime _lastQuestExtractUtc = DateTime.MinValue;
	private DateTime _lastWorkbenchExtractUtc = DateTime.MinValue;
	private DateTime _lastBlueprintExtractUtc = DateTime.MinValue;
	private DateTime _lastMapExtractUtc = DateTime.MinValue;
	private DateTime _lastDebugSaveUtc = DateTime.MinValue;
	private DateTime _lastMapViewCheckUtc = DateTime.MinValue;
	private DateTime _lastInRaidHudExtractUtc = DateTime.MinValue;
	private DetectedState _lastDetectedState = DetectedState.Unknown;
	private string? _lastDetectedMapId;
	private const int ExtractCooldownMs = 3000;
	private const int MapViewCheckCooldownMs = 1000; // Only check map view state once per second
	private const int DebugSaveCooldownMs = 30000; // Only save debug images every 30 seconds
	private const int MapCaptureRequestDebounceMs = 1200;
	private const int MapCaptureOpenDelayMs = 250;
	private const int MapCaptureRetryCount = 2;
	private const int MapCaptureRetryDelayMs = 200;
	private const int MapCaptureWindowMs = 5000;
	private const int MapViewAutoOpenCooldownMs = 5000;
	private const int MapViewAutoOpenWindowMs = 1500;
	private const int IdleInRaidSkipMs = 5000;
	private const int InRaidFullScanIntervalMs = 10000;
	private const int InRaidHudExtractCooldownMs = 2000;
	private readonly object _mapCaptureRequestLock = new();
	private bool _mapCaptureInProgress;
	private DateTime _lastMapCaptureRequestUtc = DateTime.MinValue;
	private DateTime _mapCaptureWindowUntilUtc = DateTime.MinValue;
	private bool _autoMapCaptureEnabled = false;
	private int _captureInProgress;
	private DateTime _lastFullScanUtc = DateTime.MinValue;
	private DateTime _lastMapViewAutoOpenUtc = DateTime.MinValue;
	private string? _pendingWorkbenchSignature;
	private int _pendingWorkbenchHits;
	private string? _pendingBlueprintSignature;
	private int _pendingBlueprintHits;
	
	public event Action<DetectedState>? StateDetected;
	
	public enum DetectedState {
		MainMenu,           // Main menu with quests panel, PLAY button (quest detection here)
		QuestMenu,          // Legacy - kept for compatibility
		WorkshopMenu,       // Workshop with crafting stations (workshop levels here)
		WorkbenchMenu,      // Legacy - kept for compatibility
		SkillTreeMenu,      // Skill tree view (skill points here)
		BlueprintMenu,
		TrackedResourcesMenu,
		MatchmakingQueue,   // Queue screen showing map name (map preload here)
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
		Logger.LogInfo($"State detection started (interval: {CaptureIntervalMs}ms)");
	}
	
	public void Stop() {
		if (!_isEnabled) return;
		_isEnabled = false;
		_captureTimer.Stop();
		Logger.LogInfo("State detection stopped");
	}

	public void SetAutoMapCaptureEnabled(bool enabled) {
		_autoMapCaptureEnabled = enabled;
		Logger.LogInfo($"Auto map capture {(enabled ? "enabled" : "disabled")}");
	}

	public void RequestMapCaptureFromHotkey() {
		lock (_mapCaptureRequestLock) {
			if ((DateTime.UtcNow - _lastMapCaptureRequestUtc).TotalMilliseconds < MapCaptureRequestDebounceMs) return;
			_lastMapCaptureRequestUtc = DateTime.UtcNow;
			_mapCaptureWindowUntilUtc = DateTime.UtcNow.AddMilliseconds(MapCaptureWindowMs);
			if (_mapCaptureInProgress) return;
			_mapCaptureInProgress = true;
		}
		if (RatConfig.LogDebug) {
			Logger.LogDebug($"Map capture window opened for {MapCaptureWindowMs}ms");
		}

		_ = Task.Run(async () => {
			try {
				await Task.Delay(MapCaptureOpenDelayMs).ConfigureAwait(false);
				bool captured = CaptureMapViewOnce();
				int retries = 0;
				while (!captured && retries < MapCaptureRetryCount) {
					retries++;
					await Task.Delay(MapCaptureRetryDelayMs).ConfigureAwait(false);
					captured = CaptureMapViewOnce();
				}
			} catch (Exception ex) {
				Logger.LogDebug($"Map capture request failed: {ex.Message}");
			} finally {
				lock (_mapCaptureRequestLock) {
					_mapCaptureInProgress = false;
				}
			}
		});
	}

	public async Task<bool> RunManualMapCalibration() {
		try {
			await Task.Delay(2000); // Give user time to open map if they clicked "Calibrate"
            // Or if we prompt them "Open map and press OK", the delay might be shorter or 0.
            // But let's assume immediate action after UI interaction.

			var screen = System.Windows.Forms.Screen.PrimaryScreen;
			if (screen == null) {
				Logger.LogWarning("Manual calibration failed: no primary screen detected");
				Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
				return false;
			}
			var bounds = screen.Bounds;
			using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
			
			Logger.LogInfo("========== MANUAL CALIBRATION START ==========");
			
			// First try normal extraction
			var map = ExtractMapNameFromInRaidView(bitmap);
			if (map != null) {
				Logger.LogInfo($"Map detected: {map.GetName()} ({map.Id})");
				
				// Try to detect position
				if (TryDetectPlayerPositionFromMapView(bitmap, map, out double xPercent, out double yPercent)) {
					MapOverlayManager.Instance.UpdatePosition(xPercent, yPercent, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"✓ Manual calibration successful: {map.GetName()} at ({xPercent:0.1}%, {yPercent:0.1}%)");
					Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
					return true;
				}
				
				// Position detection failed but we have the map
				MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id, map.GetName(), map.Image);
				Logger.LogWarning($"⚠ Map detected but position not found: {map.GetName()}");
				Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
				return true;
			}
			
			// Map name not detected - try all known maps
			Logger.LogWarning("Map name not detected from screen. Trying all known maps...");
			var allMaps = RaidTheoryDataSource.LoadMaps();
			
			foreach (var testMap in allMaps) {
				Logger.LogDebug($"Testing map: {testMap.GetName()} ({testMap.Id})");
				
				if (TryDetectPlayerPositionFromMapView(bitmap, testMap, out double xPercent, out double yPercent)) {
					MapOverlayManager.Instance.UpdatePosition(xPercent, yPercent, testMap.Id, testMap.GetName(), testMap.Image);
					Logger.LogInfo($"✓ Manual calibration successful (fallback): {testMap.GetName()} at ({xPercent:0.1}%, {yPercent:0.1}%)");
					Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
					return true;
				}
			}
			
			Logger.LogError("❌ Manual calibration failed: Could not detect map or position");
			Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
            return false;
		} catch (Exception ex) {
			Logger.LogError("Manual calibration exception", ex);
			Logger.LogInfo("========== MANUAL CALIBRATION END ==========");
            return false;
		}
	}
	
	private static int _timerTickCount;
	private async void OnCaptureTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e) {
		int tick = Interlocked.Increment(ref _timerTickCount);
		if (tick <= 3 || tick % 20 == 0) {
			Logger.LogInfo($"[StateDetect] tick #{tick}, enabled={_isEnabled}");
		}
		if (!_isEnabled) return;
		if (PlayerStateManager.GetState().IsInRaid && (DateTime.UtcNow - UserActivityHelper.LastInputUtc).TotalMilliseconds > IdleInRaidSkipMs) return;
		if (Volatile.Read(ref _captureInProgress) == 1) return;
		
		try {
			_lastCapture = DateTime.UtcNow;
			await Task.Run(() => CaptureAndAnalyzeScreen());
		} catch (Exception ex) {
			Logger.LogDebug($"State detection error: {ex.Message}");
		}
	}
	
	private static int _captureCount;
	private void CaptureAndAnalyzeScreen() {
		try {
			if (Interlocked.Exchange(ref _captureInProgress, 1) == 1) return;
			lock (_mapCaptureRequestLock) {
				if (_mapCaptureInProgress) return;
			}
			// Capture full screen
			var screen = System.Windows.Forms.Screen.PrimaryScreen;
			if (screen == null) {
				Logger.LogWarning("Screen capture skipped: no primary screen detected");
				return;
			}
			var bounds = screen.Bounds;
			int captureNum = Interlocked.Increment(ref _captureCount);
			if (captureNum <= 3 || captureNum % 20 == 0) {
				Logger.LogInfo($"[StateDetect] Capture #{captureNum} - screen: {bounds.Width}x{bounds.Height}");
			}
			using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
			
			// Analyze the capture to detect UI state
			var state = AnalyzeScreenCaptureWithPolicy(bitmap);
			HandleStateTransition(state);
			if (state != DetectedState.Unknown) {
				StateDetected?.Invoke(state);
				ProcessDetectedState(state, bitmap);

				// If quest menu is visible but MAP tab is also present, update map position only when requested or auto-enabled
				if (state == DetectedState.QuestMenu && ContainsMapViewIndicators(bitmap)
					&& (_autoMapCaptureEnabled || IsMapCaptureWindowActive())) {
					Bitmap mapClone = new Bitmap(bitmap);
					_ = Task.Run(() => {
						try {
							ExtractMapInfo(mapClone);
						} catch (Exception ex) {
							Logger.LogDebug($"Map extraction (quest menu) failed: {ex.Message}");
						} finally {
							mapClone.Dispose();
						}
					});
				}
			} else if (_autoMapCaptureEnabled) {
				// Attempt auto map detection only if map view indicators are present
				if (ContainsMapViewIndicators(bitmap)) {
					TryAutoMapExtract(bitmap, "Auto", out _);
				}
			}
		} catch (Exception ex) {
			Logger.LogDebug($"Screen capture failed: {ex.Message}");
		} finally {
			Interlocked.Exchange(ref _captureInProgress, 0);
		}
	}

	private DetectedState AnalyzeScreenCaptureWithPolicy(Bitmap bitmap) {
		bool isRaid = PlayerStateManager.GetState().IsInRaid;
		bool runFull = !isRaid || (DateTime.UtcNow - _lastFullScanUtc).TotalMilliseconds >= InRaidFullScanIntervalMs;
		if (runFull) {
			if (isRaid) {
				_lastFullScanUtc = DateTime.UtcNow;
			}
			LogCaptureReason(isRaid ? "full scan (in-raid interval)" : "full scan (not in raid)");
			return AnalyzeScreenCapture(bitmap);
		}

		LogCaptureReason("fast scan (in-raid)");
		return AnalyzeScreenCaptureFastInRaid(bitmap);
	}

	private void LogCaptureReason(string reason) {
		if (!RatConfig.LogDebug) return;
		Logger.LogDebug($"Capture reason: {reason}");
	}

	private DetectedState AnalyzeScreenCaptureFastInRaid(Bitmap bitmap) {
		if (ContainsMapViewIndicators(bitmap)) {
			return DetectedState.MapView;
		}
		if (ContainsInRaidIndicators(bitmap)) {
			return DetectedState.InRaid;
		}
		return DetectedState.Unknown;
	}

	private bool CaptureMapViewOnce() {
		try {
			var screen = System.Windows.Forms.Screen.PrimaryScreen;
			if (screen == null) {
				Logger.LogWarning("Map capture skipped: no primary screen detected");
				return false;
			}
			var bounds = screen.Bounds;
			using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
			using var graphics = Graphics.FromImage(bitmap);
			graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

			if (!ContainsMapViewIndicators(bitmap)) {
				Logger.LogDebug("Map capture skipped: map view not detected");
				return false;
			}

			ExtractMapInfo(bitmap);
			return true;
		} catch (Exception ex) {
			Logger.LogDebug($"Map capture failed: {ex.Message}");
			return false;
		}
	}

	private void HandleStateTransition(DetectedState current) {
		if (current == DetectedState.Unknown) return;
		if (current == _lastDetectedState) return;
		Logger.LogInfo($"UI state changed: {_lastDetectedState} -> {current}");
		
		bool isRaidState = current == DetectedState.MapView || current == DetectedState.InRaid;
		PlayerStateManager.SetUiState(current, isRaidState);
		MapOverlayManager.Instance.SetRaidState(isRaidState);
		
		bool wasRaidState = _lastDetectedState == DetectedState.MapView || _lastDetectedState == DetectedState.InRaid;
		if (!isRaidState && wasRaidState) {
			MapOverlayManager.Instance.ClearPosition();
		}
		
		_lastDetectedState = current;
	}
	
	private DetectedState AnalyzeScreenCapture(Bitmap bitmap) {
		// Priority-based detection - check most specific states first
		
		// Check for Skill Tree menu (has "SKILL TREE" header)
		if (ContainsSkillTreeIndicators(bitmap)) {
			return DetectedState.SkillTreeMenu;
		}
		
		// Check for Matchmaking/Queue screen (shows map name, MATCHMAKING text, timer)
		if (ContainsMatchmakingIndicators(bitmap)) {
			return DetectedState.MatchmakingQueue;
		}
		
		// Check for Workshop menu (crafting stations at bottom, WORKSHOP tab highlighted)
		if (ContainsWorkshopMenuIndicators(bitmap)) {
			return DetectedState.WorkshopMenu;
		}

		// Check for in-raid HUD (no menu open)
		if (ContainsInRaidIndicators(bitmap)) {
			return DetectedState.InRaid;
		}
		
		// Check for Main Menu (QUESTS panel on left, PLAY button visible)
		// Quest detection should ONLY happen here
		if (ContainsMainMenuIndicators(bitmap)) {
			return DetectedState.MainMenu;
		}
		
		// Legacy: Check for quest menu indicators (kept for backwards compatibility)
		if (EnableLegacyQuestMenuDetection && ContainsQuestMenuIndicators(bitmap)) {
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
	
	private bool ContainsSkillTreeIndicators(Bitmap bitmap) {
		// Look for "SKILL TREE" header text or skill branch names
		string topText = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.15), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string normalized = NormalizeText(topText);
		
		// Primary indicator: "SKILL TREE" header
		if (normalized.Contains("skill tree") || normalized.Contains("skilltree")) {
			return true;
		}
		
		// Secondary: check for skill branch names in center area
		string centerText = ReadOcrInRegion(bitmap, GetCenterRegion(bitmap, 0.6, 0.5), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ");
		string centerNorm = NormalizeText(centerText);
		
		int branchCount = 0;
		if (centerNorm.Contains("conditioning")) branchCount++;
		if (centerNorm.Contains("mobility")) branchCount++;
		if (centerNorm.Contains("survival")) branchCount++;
		
		// If we see at least 2 branch names, it's likely the skill tree
		return branchCount >= 2;
	}
	
	private bool ContainsMatchmakingIndicators(Bitmap bitmap) {
		// Look for "MATCHMAKING" text at bottom or map name in bottom-right panel
		string bottomText = ReadOcrInRegion(bitmap, GetBottomRegion(bitmap, 0.15), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789: ");
		string normalized = NormalizeText(bottomText);
		
		// Primary indicator: "MATCHMAKING" text
		if (normalized.Contains("matchmaking")) {
			return true;
		}
		
		// Check for timer pattern (00:00 format) near bottom
		if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\d{2}:\d{2}")) {
			// Also check for "CANCEL" button which appears during matchmaking
			if (normalized.Contains("cancel")) {
				return true;
			}
		}
		
		// Check bottom-right panel for map name during queue
		var bottomRightRegion = GetBottomRightRegion(bitmap, 0.35, 0.25);
		string bottomRightText = ReadOcrInRegion(bitmap, bottomRightRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		
		// Look for known map names in the queue panel
		var mapMatch = MatchMapFromText(bottomRightText);
		if (mapMatch != null && normalized.Contains("cancel")) {
			return true;
		}
		
		return false;
	}
	
	private bool ContainsWorkshopMenuIndicators(Bitmap bitmap) {
		// Avoid false positives when in-raid map is open
		if (ContainsMapViewIndicators(bitmap)) return false;
		
		// Workshop has "WORKSHOP" tab highlighted at top and crafting stations at bottom
		string topText = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.08), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string normalized = NormalizeText(topText);
		
		// Check for WORKSHOP in the top nav bar
		if (!normalized.Contains("workshop")) {
			return false;
		}
		
		// Also check for "BLUEPRINTS" hint at bottom (R key prompt)
		string bottomText = ReadOcrInRegion(bitmap, GetBottomRegion(bitmap, 0.08), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string bottomNorm = NormalizeText(bottomText);
		
		// Workshop-specific: has BLUEPRINTS key hint
		return bottomNorm.Contains("blueprint");
	}
	
	private bool ContainsMainMenuIndicators(Bitmap bitmap) {
		// Avoid false positives when in-raid HUD is visible
		if (ContainsInRaidIndicators(bitmap)) return false;

		// Main menu has QUESTS panel on left, PLAY button, player status
		string leftText = ReadOcrInRegion(bitmap, GetLeftRegion(bitmap, 0.35, 0.6), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string leftNorm = NormalizeText(leftText);
		
		// Must have QUESTS panel
		bool hasQuestPanel = leftNorm.Contains("quest") && (leftNorm.Contains("active") || leftNorm.Contains("completed") || leftNorm.Contains("quest log"));
		if (!hasQuestPanel) {
			return false;
		}
		
		// Check for PLAY button or player status indicators on right
		string rightText = ReadOcrInRegion(bitmap, GetRightRegion(bitmap, 0.40, 0.6), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string rightNorm = NormalizeText(rightText);
		
		// Main menu indicators: PLAY button, Fill Squad, Ready/Idle status
		bool hasPlayButton = rightNorm.Contains("play");
		bool hasSquadOption = rightNorm.Contains("fill squad") || rightNorm.Contains("squad");
		bool hasPlayerStatus = rightNorm.Contains("idle") || rightNorm.Contains("ready");
		
		return hasPlayButton || hasSquadOption || hasPlayerStatus;
	}

	private bool ContainsInRaidIndicators(Bitmap bitmap) {
		// In-raid HUD typically shows UNARMED/FLASHLIGHT, ammo/weapon name on bottom-right
		var bottomRight = GetBottomRightRegion(bitmap, 0.30, 0.25);
		string bottomRightText = ReadOcrInRegion(bitmap, bottomRight, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ");
		string bottomRightNorm = NormalizeText(bottomRightText);

		if (bottomRightNorm.Contains("unarmed") || bottomRightNorm.Contains("flashlight")) {
			return true;
		}

		// Also check for ammo count patterns like "001" or "039" near weapon HUD
		if (Regex.IsMatch(bottomRightNorm, @"\b\d{2,3}\b")) {
			// Avoid matching menu numbers by requiring weapon HUD keywords
			if (bottomRightNorm.Contains("ammo") || bottomRightNorm.Contains("ferro")) {
				return true;
			}
		}

		return false;
	}
	
	private static Rectangle GetBottomRightRegion(Bitmap bitmap, double widthRatio, double heightRatio) {
		int width = Math.Max(1, (int)(bitmap.Width * widthRatio));
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(bitmap.Width - width, bitmap.Height - height, width, height);
	}

	private static Rectangle GetTopRightRegion(Bitmap bitmap, double widthRatio, double heightRatio) {
		int width = Math.Max(1, (int)(bitmap.Width * widthRatio));
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(bitmap.Width - width, 0, width, height);
	}

	private bool ContainsQuestMenuIndicators(Bitmap bitmap) {
		// Avoid false positives from in-raid HUD quest tracker
		if (ContainsInRaidIndicators(bitmap)) return false;

		// TODO: Implement OCR-based detection for quest menu
		// Look for: "QUESTS", "ACTIVE", "COMPLETED" text
		// Look for quest list layout patterns
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.25), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
		string normalized = NormalizeText(text);
		bool hasQuests = normalized.Contains("quest");
		bool hasActiveCompleted = normalized.Contains("active") || normalized.Contains("completed");
		return hasQuests && hasActiveCompleted;
	}
	
	private bool ContainsWorkbenchMenuIndicators(Bitmap bitmap) {
		// Avoid false positives from in-raid nav like "CRAFTING"
		if (ContainsMapViewIndicators(bitmap)) return false;
		
		// TODO: Implement detection for workbench interface
		// Look for: workbench name, level indicator, "CRAFT" button
		string text = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.35), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		return text.Contains("WORKBENCH", StringComparison.OrdinalIgnoreCase)
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
		// In-raid map view has:
		// - Top nav bar with INVENTORY, CRAFTING, MAP (highlighted), LOGBOOK, SYSTEM
		// - Map name + timer in top-right (e.g., "DAM BATTLEGROUNDS - 18:40")
		// - Player marker (cyan arrow) on map
		// - Location names on map (Victory Ridge, The Dam, Red Lakes, etc.)
		
		// Rate limit expensive logging to prevent spam
		bool shouldLog = (DateTime.UtcNow - _lastMapViewCheckUtc).TotalMilliseconds >= MapViewCheckCooldownMs;
		
		var topRegion = GetTopRegion(bitmap, 0.10);
		string topText = ReadOcrInRegion(bitmap, topRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		string normalized = NormalizeText(topText);

		if (shouldLog) {
			Logger.LogInfo($"[MapCheck] Top OCR region: {topRegion.Width}x{topRegion.Height} at ({topRegion.X},{topRegion.Y})");
			Logger.LogInfo($"[MapCheck] Top text: '{normalized.Replace("\n", " | ")}'");
		}

		// Check for MAP tab in navigation bar (OCR often misreads "MAP" as "an", "m", etc.)
		bool hasMapTab = normalized.Contains("map", StringComparison.OrdinalIgnoreCase);
		
		// Check for other nav tabs to confirm we're in the in-game menu
		int navHits = 0;
		if (hasMapTab) navHits++;
		if (normalized.Contains("inventory", StringComparison.OrdinalIgnoreCase)) navHits++;
		if (normalized.Contains("crafting", StringComparison.OrdinalIgnoreCase)) navHits++;
		if (normalized.Contains("logbook", StringComparison.OrdinalIgnoreCase)) navHits++;
		if (normalized.Contains("system", StringComparison.OrdinalIgnoreCase)) navHits++;

		if (shouldLog) {
			Logger.LogInfo($"[MapCheck] hasMapTab: {hasMapTab}, navHits: {navHits}");
		}

		// If we see 3+ nav tabs (inventory, crafting, system), we're in the in-raid menu
		// OCR often misreads the highlighted MAP tab and sometimes misses logbook
		bool inRaidMenu = navHits >= 3 || (hasMapTab && navHits >= 2);
		
		if (inRaidMenu) {
			var topRightRegion = GetTopRightRegion(bitmap, 0.35, 0.25);
			string topRightText = ReadOcrInRegion(bitmap, topRightRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:- ");
			string topRightNorm = NormalizeText(topRightText);
			bool hasTimer = Regex.IsMatch(topRightNorm, @"\b\d{1,2}:\d{2}\b");
			bool hasMapName = MatchMapFromText(topRightText) != null;
			
			if (shouldLog) {
				Logger.LogInfo($"[MapCheck] Top-right text: '{topRightNorm.Replace("\n", " | ")}'");
				Logger.LogInfo($"[MapCheck] hasTimer: {hasTimer}, hasMapName: {hasMapName}");
			}
			
			if (hasTimer || hasMapName) {
				if (shouldLog) {
					Logger.LogInfo("✓ Map view detected (nav tabs + timer/map name)");
					_lastMapViewCheckUtc = DateTime.UtcNow;
				}
				return true;
			}
			
			// Also check for player marker (cyan arrow) as additional confirmation
			Rectangle mapRegion = GetCenterRegion(bitmap, 0.6, 0.7);
			if (mapRegion.Width >= 50 && mapRegion.Height >= 50) {
				try {
					using Bitmap mapView = bitmap.Clone(mapRegion, PixelFormat.Format24bppRgb);
					if (TryFindPlayerMarker(mapView, out _)) {
						if (shouldLog) {
							Logger.LogInfo("✓ Map view detected (nav tabs + player marker)");
							_lastMapViewCheckUtc = DateTime.UtcNow;
						}
						return true;
					}
				} catch {
					// ignore and fall through
				}
			}
		}

	// REMOVED: Fallback detection based on cyan pixels alone
	// This was causing false positives when other cyan UI elements were on screen
	// Now we REQUIRE the in-raid menu nav tabs to be visible to detect map view
	
	if (shouldLog) {
		Logger.LogInfo("[MapCheck] Map view NOT detected");
		_lastMapViewCheckUtc = DateTime.UtcNow;
	}
	return false;
}

	private void ProcessDetectedState(DetectedState state, Bitmap bitmap) {
	Logger.LogInfo($"[StateDetect] Detected state: {state}");
	
	// Clone bitmap for async processing to avoid object disposed exception
	// when the calling method disposes the original bitmap.
	Bitmap clone = new Bitmap(bitmap);

	_ = Task.Run(() => {
		try {
			switch (state) {
				case DetectedState.MainMenu:
					// Quest detection ONLY in main menu
					ExtractQuestInfo(clone);
					break;
				case DetectedState.QuestMenu:
					// Legacy support
					ExtractQuestInfo(clone);
					break;
				case DetectedState.WorkshopMenu:
					// Workshop levels from workshop menu
					ExtractWorkshopLevels(clone);
					break;
				case DetectedState.WorkbenchMenu:
					// Legacy support
					ExtractWorkbenchInfo(clone);
                        break;
                    case DetectedState.SkillTreeMenu:
                        // Skill tree data extraction
                        ExtractSkillTreeInfo(clone);
                        break;
                    case DetectedState.MatchmakingQueue:
                        // Map preload from queue screen
                        ExtractQueueMapInfo(clone);
                        break;
                    case DetectedState.BlueprintMenu:
                        ExtractBlueprintInfo(clone);
                        break;
                    case DetectedState.TrackedResourcesMenu:
                        ExtractTrackedResourcesInfo(clone);
                        break;
					case DetectedState.MapView:
						if (_autoMapCaptureEnabled || IsMapCaptureWindowActive()) {
							ExtractMapInfo(clone);
							break;
						}
						if (TryOpenMapCaptureWindowFromDetection()) {
							ExtractMapInfo(clone);
							break;
						}
						if (RatConfig.LogDebug && ContainsMapViewIndicators(clone)) {
							Logger.LogDebug("Map view detected but capture disabled (no window + auto off)");
						}
						break;
					case DetectedState.InRaid:
						ExtractInRaidHudInfo(clone);
						break;
                }
            } catch (Exception ex) {
                Logger.LogError($"Error processing state {state}: {ex.Message}", ex);
            } finally {
                clone.Dispose();
            }
        });
	}

		private bool IsMapCaptureWindowActive() {
			lock (_mapCaptureRequestLock) {
				return DateTime.UtcNow <= _mapCaptureWindowUntilUtc;
			}
		}

		private bool TryOpenMapCaptureWindowFromDetection() {
			lock (_mapCaptureRequestLock) {
				if ((DateTime.UtcNow - _lastMapViewAutoOpenUtc).TotalMilliseconds < MapViewAutoOpenCooldownMs) return false;
				_mapCaptureWindowUntilUtc = DateTime.UtcNow.AddMilliseconds(MapViewAutoOpenWindowMs);
				_lastMapViewAutoOpenUtc = DateTime.UtcNow;
				if (RatConfig.LogDebug) {
					Logger.LogDebug($"Map capture window auto-opened for {MapViewAutoOpenWindowMs}ms");
				}
				return true;
			}
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
				var newActive = new HashSet<string>(matches.Select(q => q.Id), StringComparer.OrdinalIgnoreCase);
				var currentActive = new HashSet<string>(PlayerStateManager.GetState().ActiveQuests, StringComparer.OrdinalIgnoreCase);
				if (!newActive.SetEquals(currentActive)) {
					PlayerStateManager.SetActiveQuests(newActive);
					Logger.LogInfo($"Detected {matches.Count} active quests from screen");
				}
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Quest extraction failed: {ex.Message}");
		}
	}
	
	private void ExtractWorkbenchInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastWorkbenchExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastWorkbenchExtractUtc = DateTime.UtcNow;

			var lowerRegion = GetBottomRegion(bitmap, 0.35);
			string ocrText = ReadOcrInRegion(bitmap, lowerRegion, PageSegMode.Auto, "IVXL0123456789");
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegionInverted(bitmap, lowerRegion, PageSegMode.Auto, "IVXL0123456789");
			}
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

			if (map.Count > 0 && TryApplyStableWorkbenchLevels(map, true)) {
				Logger.LogInfo($"Detected {map.Count} workbench levels from screen");
			}
			
			ExtractWorkbenchCraftables(bitmap);
		} catch (Exception ex) {
			Logger.LogWarning($"Workbench extraction failed: {ex.Message}");
		}
	}

	private static readonly object ItemNameLookupLock = new();
	private static Dictionary<string, string>? ItemNameLookup;
	private static readonly object ProjectNameLookupLock = new();
	private static Dictionary<string, string>? ProjectNameLookup;

	private void ExtractWorkbenchCraftables(Bitmap bitmap) {
		try {
			var itemRegion = new Rectangle(
				(int)(bitmap.Width * 0.08),
				(int)(bitmap.Height * 0.22),
				(int)(bitmap.Width * 0.70),
				(int)(bitmap.Height * 0.60));
			
			string ocrText = ReadOcrInRegion(bitmap, itemRegion, PageSegMode.SparseText, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegion(bitmap, itemRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			}
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegionInverted(bitmap, itemRegion, PageSegMode.SparseText, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			}
			if (string.IsNullOrWhiteSpace(ocrText)) return;
			
			var lines = ocrText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length >= 3)
				.ToList();
			if (lines.Count == 0) return;
			
			var lookup = GetItemNameLookup();
			var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			
			foreach (var line in lines) {
				string normalizedLine = NormalizeText(line);
				if (normalizedLine.Length < 3) continue;
				
				foreach (var kvp in lookup) {
					if (ContainsWholeWord(normalizedLine, kvp.Key)) {
						matchedIds.Add(kvp.Value);
					}
				}
			}
			
			if (matchedIds.Count == 0) return;
			string? stationId = TryDetectWorkbenchStation(bitmap);
			PlayerStateManager.SetCraftableItems(stationId, matchedIds);
		} catch (Exception ex) {
			Logger.LogWarning($"Workbench craftable extraction failed: {ex.Message}");
		}
	}

	private static Dictionary<string, string> GetItemNameLookup() {
		lock (ItemNameLookupLock) {
			if (ItemNameLookup != null) return ItemNameLookup;
			ItemNameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var item in ArcRaidersData.GetItems()) {
				if (!string.IsNullOrWhiteSpace(item.Name)) {
					string norm = NormalizeText(item.Name);
					if (norm.Length >= 4 && !ItemNameLookup.ContainsKey(norm)) {
						ItemNameLookup[norm] = item.Id;
					}
				}
				if (!string.IsNullOrWhiteSpace(item.ShortName)) {
					string normShort = NormalizeText(item.ShortName);
					if (normShort.Length >= 4 && !ItemNameLookup.ContainsKey(normShort)) {
						ItemNameLookup[normShort] = item.Id;
					}
				}
			}
			return ItemNameLookup;
		}
	}

	private static Dictionary<string, string> GetProjectNameLookup() {
		lock (ProjectNameLookupLock) {
			if (ProjectNameLookup != null) return ProjectNameLookup;
			ProjectNameLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var project in ArcRaidersData.GetProjects()) {
				string name = project.GetName();
				if (string.IsNullOrWhiteSpace(name)) continue;
				string norm = NormalizeText(name);
				if (norm.Length >= 4 && !ProjectNameLookup.ContainsKey(norm)) {
					ProjectNameLookup[norm] = project.Id;
				}
			}
			return ProjectNameLookup;
		}
	}

	private static bool ContainsWholeWord(string haystack, string needle) {
		if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
		string padded = " " + haystack + " ";
		string target = " " + needle + " ";
		return padded.Contains(target, StringComparison.OrdinalIgnoreCase);
	}

	private string? TryDetectWorkbenchStation(Bitmap bitmap) {
		try {
			string topText = ReadOcrInRegion(bitmap, GetTopRegion(bitmap, 0.20), PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ ");
			if (string.IsNullOrWhiteSpace(topText)) return null;
			string normalized = NormalizeText(topText);
			if (string.IsNullOrWhiteSpace(normalized)) return null;
			
			var modules = ArcRaidersData.GetHideoutModules();
			foreach (var module in modules) {
				string name = module.GetName();
				if (string.IsNullOrWhiteSpace(name)) continue;
				string norm = NormalizeText(name);
				if (norm.Length < 3) continue;
				if (normalized.Contains(norm, StringComparison.OrdinalIgnoreCase)) {
					return module.Id;
				}
			}
		} catch {
			// ignore
		}
		return null;
	}
	
	private void ExtractBlueprintInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastBlueprintExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastBlueprintExtractUtc = DateTime.UtcNow;

			var listRegion = new Rectangle(
				(int)(bitmap.Width * 0.08),
				(int)(bitmap.Height * 0.18),
				(int)(bitmap.Width * 0.84),
				(int)(bitmap.Height * 0.70));

			string ocrText = ReadOcrInRegion(bitmap, listRegion, PageSegMode.SparseText, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegion(bitmap, listRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			}
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegionInverted(bitmap, listRegion, PageSegMode.SparseText, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			}
			if (string.IsNullOrWhiteSpace(ocrText)) return;

			var lines = ocrText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length >= 3)
				.ToList();
			if (lines.Count == 0) return;

			var lookup = GetProjectNameLookup();
			var learnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string[] learnedKeywords = { "learned", "unlocked", "known" };

			var normalizedLines = lines.Select(NormalizeText).ToList();
			var statusLines = normalizedLines
				.Select(line => learnedKeywords.Any(k => line.Contains(k)))
				.ToList();

			for (int i = 0; i < normalizedLines.Count; i++) {
				string normalizedLine = normalizedLines[i];
				bool learnedLine = statusLines[i]
					|| (i > 0 && statusLines[i - 1])
					|| (i + 1 < statusLines.Count && statusLines[i + 1]);

				if (!learnedLine) continue;

				foreach (var kvp in lookup) {
					if (ContainsWholeWord(normalizedLine, kvp.Key)) {
						learnedIds.Add(kvp.Value);
					}
				}
			}

			if (learnedIds.Count == 0) return;

			string signature = string.Join("|", learnedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
			if (string.Equals(_pendingBlueprintSignature, signature, StringComparison.Ordinal)) {
				_pendingBlueprintHits++;
				if (_pendingBlueprintHits >= 2) {
					foreach (var id in learnedIds) {
						PlayerStateManager.LearnBlueprint(id);
					}
					_pendingBlueprintSignature = null;
					_pendingBlueprintHits = 0;
					Logger.LogInfo($"Detected {learnedIds.Count} learned blueprints from screen");
				}
				return;
			}

			_pendingBlueprintSignature = signature;
			_pendingBlueprintHits = 1;
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

	private void ExtractInRaidHudInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastInRaidHudExtractUtc).TotalMilliseconds < InRaidHudExtractCooldownMs) return;
			_lastInRaidHudExtractUtc = DateTime.UtcNow;

			var hudRegion = GetBottomRightRegion(bitmap, 0.30, 0.25);
			string ocrText = ReadOcrInRegion(bitmap, hudRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/ ");
			if (string.IsNullOrWhiteSpace(ocrText)) {
				ocrText = ReadOcrInRegionInverted(bitmap, hudRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/ ");
			}
			if (string.IsNullOrWhiteSpace(ocrText)) return;

			int? ammoInMag = null;
			int? ammoReserve = null;
			string? weaponLabel = null;

			var ammoMatch = Regex.Match(ocrText, @"\b(\d{1,3})\s*/\s*(\d{1,3})\b");
			if (ammoMatch.Success
				&& int.TryParse(ammoMatch.Groups[1].Value, out int mag)
				&& int.TryParse(ammoMatch.Groups[2].Value, out int reserve)) {
				ammoInMag = mag;
				ammoReserve = reserve;
			} else {
				var numberMatches = Regex.Matches(ocrText, @"\b\d{1,3}\b");
				if (numberMatches.Count >= 2
					&& int.TryParse(numberMatches[0].Value, out int mag2)
					&& int.TryParse(numberMatches[1].Value, out int reserve2)) {
					ammoInMag = mag2;
					ammoReserve = reserve2;
				}
			}

			var lines = ocrText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length >= 3)
				.ToList();
			foreach (var line in lines) {
				if (!Regex.IsMatch(line, "[A-Za-z]")) continue;
				if (line.Contains("ammo", StringComparison.OrdinalIgnoreCase)) continue;
				if (line.Contains("unarmed", StringComparison.OrdinalIgnoreCase)) continue;
				if (line.Contains("flashlight", StringComparison.OrdinalIgnoreCase)) continue;
				weaponLabel = line;
				break;
			}

			PlayerStateManager.SetInRaidHud(weaponLabel, ammoInMag, ammoReserve);
			if (RatConfig.LogDebug) {
				Logger.LogDebug($"HUD: weapon='{weaponLabel}', ammo={ammoInMag}/{ammoReserve}");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"In-raid HUD extraction failed: {ex.Message}");
		}
	}

	private DateTime _lastSkillTreeExtractUtc = DateTime.MinValue;
	private DateTime _lastWorkshopExtractUtc = DateTime.MinValue;
	private DateTime _lastQueueMapExtractUtc = DateTime.MinValue;
	
	/// <summary>
	/// Extract workshop/workbench levels from the workshop menu screen
	/// The workshop shows crafting stations at the bottom with level indicators (I, II, III, etc.)
	/// </summary>
	private void ExtractWorkshopLevels(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastWorkshopExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastWorkshopExtractUtc = DateTime.UtcNow;

			// Workshop stations are displayed at the bottom of the screen
			// Each station has a Roman numeral or number indicating level
			// Expanded region from 25% to 40% to capture all stations
			var stationLevels = ExtractStationLevelsFromRow(bitmap);
			var namedLevels = stationLevels
				.Where(s => s.Level > 0 && !string.IsNullOrWhiteSpace(s.StationId))
				.GroupBy(s => s.StationId!, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.Max(v => v.Level), StringComparer.OrdinalIgnoreCase);

			if (namedLevels.Count >= 2) {
				if (TryApplyStableWorkbenchLevels(namedLevels, false)) {
					Logger.LogInfo($"Workshop: Detected {namedLevels.Count} station levels from screen");
				}
				return;
			}

			var levels = stationLevels.Select(s => s.Level).ToList();
			if (levels.Count == 0 || levels.All(l => l == 0)) {
				// Fallback to full-row OCR if slot OCR fails
				var bottomRegion = GetBottomRegion(bitmap, 0.40);
				string ocrText = ReadOcrInRegion(bitmap, bottomRegion, PageSegMode.Auto, "IVXL0123456789 ");
				if (string.IsNullOrWhiteSpace(ocrText)) return;

				Logger.LogDebug($"Workshop OCR text: {ocrText.Replace("\n", " | ")}");
				levels = ExtractWorkbenchLevels(ocrText).ToList();
				if (levels.Count == 0) {
					Logger.LogDebug("No workshop levels detected from screen");
					return;
				}
			}

			var modules = ArcRaidersData.GetHideoutModules()
				.OrderBy(m => m.GetName())
				.ToList();
			if (modules.Count == 0) return;

			int assignCount = Math.Min(levels.Count, modules.Count);
			var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < assignCount; i++) {
				map[modules[i].Id] = levels[i];
			}

			if (map.Count > 0 && TryApplyStableWorkbenchLevels(map, true)) {
				Logger.LogInfo($"Workshop: Detected {map.Count} station levels from screen");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Workshop level extraction failed: {ex.Message}");
		}
	}

	private List<StationLevel> ExtractStationLevelsFromRow(Bitmap bitmap) {
		var levels = new List<StationLevel>();
		try {
			int stationCount = 8;
			var strip = GetBottomRegion(bitmap, 0.18);
			if (strip.Height < 30) return levels;
			int slotWidth = strip.Width / stationCount;
			for (int i = 0; i < stationCount; i++) {
				int slotX = strip.X + (i * slotWidth);
				var slotRect = new Rectangle(slotX, strip.Y, slotWidth, strip.Height);
				var nameRect = new Rectangle(
					slotRect.X + (int)(slotRect.Width * 0.05),
					slotRect.Y + (int)(slotRect.Height * 0.05),
					(int)(slotRect.Width * 0.90),
					(int)(slotRect.Height * 0.40));
				// Focus on lower-right quadrant of each slot for roman numeral
				var levelRect = new Rectangle(
					slotRect.X + (int)(slotRect.Width * 0.55),
					slotRect.Y + (int)(slotRect.Height * 0.55),
					(int)(slotRect.Width * 0.40),
					(int)(slotRect.Height * 0.40));
				string ocrText = ReadOcrInRegion(bitmap, levelRect, PageSegMode.SingleLine, "IVXL0123456789 ");
				if (string.IsNullOrWhiteSpace(ocrText)) {
					ocrText = ReadOcrInRegionInverted(bitmap, levelRect, PageSegMode.SingleLine, "IVXL0123456789 ");
				}
				int level = ExtractWorkbenchLevels(ocrText).FirstOrDefault();

				string nameText = ReadOcrInRegion(bitmap, nameRect, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ");
				if (string.IsNullOrWhiteSpace(nameText)) {
					nameText = ReadOcrInRegionInverted(bitmap, nameRect, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz ");
				}
				string? stationId = TryMatchModuleIdFromText(nameText);

				levels.Add(new StationLevel {
					StationId = stationId,
					Level = level
				});
			}
		} catch {
			// ignore
		}
		return levels;
	}

	private sealed class StationLevel {
		public string? StationId { get; set; }
		public int Level { get; set; }
	}
	
	/// <summary>
	/// Extract skill tree information from the skill tree menu
	/// Looks for branch names (Conditioning, Mobility, Survival) and point allocations
	/// </summary>
	private void ExtractSkillTreeInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastSkillTreeExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastSkillTreeExtractUtc = DateTime.UtcNow;

			// Read the center area where skill branches are displayed
			var centerRegion = GetCenterRegion(bitmap, 0.8, 0.7);
			string ocrText = ReadOcrInRegion(bitmap, centerRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/ ");
			if (string.IsNullOrWhiteSpace(ocrText)) return;

			string normalized = NormalizeText(ocrText);
			
			// Extract branch totals (e.g., "CONDITIONING 23", "MOBILITY 25", "SURVIVAL 4")
			var branchPoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			
			// Look for patterns like "CONDITIONING\n23" or "CONDITIONING 23"
			var branchPatterns = new[] {
				("conditioning", @"conditioning\s*(\d+)"),
				("mobility", @"mobility\s*(\d+)"),
				("survival", @"survival\s*(\d+)")
			};
			
			foreach (var (branch, pattern) in branchPatterns) {
				var match = Regex.Match(normalized, pattern, RegexOptions.IgnoreCase);
				if (match.Success && int.TryParse(match.Groups[1].Value, out int points)) {
					branchPoints[branch] = points;
					Logger.LogDebug($"Skill Tree: {branch} = {points} points");
				}
			}
			
			// Check for "SKILL POINT AVAILABLE" indicator at top-left
			var topLeftRegion = GetLeftRegion(bitmap, 0.25, 0.15);
			string topLeftText = ReadOcrInRegion(bitmap, topLeftRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ");
			string topLeftNorm = NormalizeText(topLeftText);
			
			int availablePoints = 0;
			var availableMatch = Regex.Match(topLeftNorm, @"(\d+)\s*skill\s*point", RegexOptions.IgnoreCase);
			if (availableMatch.Success && int.TryParse(availableMatch.Groups[1].Value, out int avail)) {
				availablePoints = avail;
				Logger.LogDebug($"Skill Tree: {availablePoints} points available");
			}
			
			if (branchPoints.Count > 0 || availablePoints > 0) {
				PlayerStateManager.SetSkillTreeData(branchPoints, availablePoints);
				Logger.LogInfo($"Skill Tree: Detected {branchPoints.Count} branches, {availablePoints} available points");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Skill tree extraction failed: {ex.Message}");
		}
	}
	
	/// <summary>
	/// Extract map name from the matchmaking queue screen for preloading
	/// The map name appears in the bottom-right panel during queue (e.g., "DAM BATTLEGROUNDS")
	/// It's shown above the "Custom Loadout" text with a timer
	/// </summary>
	private void ExtractQueueMapInfo(Bitmap bitmap) {
		try {
			if ((DateTime.UtcNow - _lastQueueMapExtractUtc).TotalMilliseconds < ExtractCooldownMs) return;
			_lastQueueMapExtractUtc = DateTime.UtcNow;

			// The map name during queue is shown in a specific spot in the bottom-right
			// Format: "00:01  DAM BATTLEGROUNDS" with timer on left
			// Read from a targeted region
			var queuePanelRegion = new Rectangle(
				(int)(bitmap.Width * 0.65),   // Start at 65% from left
				(int)(bitmap.Height * 0.55),  // Start at 55% from top
				(int)(bitmap.Width * 0.35),   // Take right 35%
				(int)(bitmap.Height * 0.20)   // Take 20% height
			);
			
			string ocrText = ReadOcrInRegion(bitmap, queuePanelRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:- ");
			if (string.IsNullOrWhiteSpace(ocrText)) return;
			
			Logger.LogDebug($"Queue OCR text: {ocrText.Replace("\n", " | ")}");

			// Only match against known maps - don't create fake maps from OCR garbage
			var map = MatchMapFromText(ocrText);
			if (map != null && !string.IsNullOrWhiteSpace(map.Id) && map.Id != "unknown") {
				// Preload the map data
				bool mapChanged = !string.Equals(_lastDetectedMapId, map.Id, StringComparison.OrdinalIgnoreCase);
				if (mapChanged) {
					_lastDetectedMapId = map.Id;
					
					// Preload map to overlay manager with center position
					MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"Queue: Preloading map '{map.GetName()}' for upcoming raid");
					
					// Store in player state for reference
					PlayerStateManager.SetQueuedMap(map.Id, map.GetName());
				}
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Queue map extraction failed: {ex.Message}");
		}
	}

	private void ExtractMapInfo(Bitmap bitmap) {
		try {
			Logger.LogInfo("[ExtractMap] Starting extraction...");
			// Apply cooldown to prevent rapid repeated processing
			if ((DateTime.UtcNow - _lastMapExtractUtc).TotalMilliseconds < ExtractCooldownMs) {
				Logger.LogInfo($"[ExtractMap] Skipped - cooldown ({ExtractCooldownMs}ms)");
				return;
			}
			_lastMapExtractUtc = DateTime.UtcNow;
			
			// In-raid map: read map name from top-right panel (e.g., "DAM BATTLEGROUNDS - 17:41")
			// Then detect player position from the cyan arrow marker
			
			var map = ExtractMapNameFromInRaidView(bitmap);
			Logger.LogInfo($"[ExtractMap] ExtractMapNameFromInRaidView returned: {map?.GetName() ?? "(null)"}");
			if (map != null) {
				// Try to detect player position from the map view
				if (TryDetectPlayerPositionFromMapView(bitmap, map, out double xPercent, out double yPercent)) {
					MapOverlayManager.Instance.UpdatePosition(xPercent, yPercent, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"In-Raid Map: {map.GetName()} - Player at ({xPercent:0.0}%, {yPercent:0.0}%)");
				} else {
					// Fallback: still update map but with center position
					MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"In-Raid Map: {map.GetName()} (player position not detected)");
					
					// Save debug image for troubleshooting (only occasionally)
					SaveDebugMapImage(bitmap, "position_fail");
				}
				return;
			}
			
			Logger.LogInfo("[ExtractMap] Trying auto extraction fallback...");
			// Fallback to auto extraction
			if (TryAutoMapExtract(bitmap, "MapView", out map, updatePosition: false)) {
				Logger.LogInfo($"[ExtractMap] Auto extraction found: {map?.GetName() ?? "(null)"}");
				if (map != null && TryDetectPlayerPositionFromMapView(bitmap, map, out double xPercent2, out double yPercent2)) {
					MapOverlayManager.Instance.UpdatePosition(xPercent2, yPercent2, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"Map position updated: {map.GetName()} ({xPercent2:0.0}%, {yPercent2:0.0}%)");
				} else if (map != null) {
					MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id, map.GetName(), map.Image);
					Logger.LogInfo($"Map detected: {map.GetName()} (position set to center)");
				}
			} else {
				Logger.LogInfo("[ExtractMap] No map found by auto extraction");
			}
		} catch (Exception ex) {
			Logger.LogWarning($"Map extraction failed: {ex.Message}");
		}
	}
	
	/// <summary>
	/// Extract map name specifically from the in-raid map view
	/// The map name appears in the top-right corner with format "MAP NAME - HH:MM"
	/// </summary>
	private RaidTheoryDataSource.RaidTheoryMap? ExtractMapNameFromInRaidView(Bitmap bitmap) {
		try {
			// Read from top-right corner where "DAM BATTLEGROUNDS - 17:41" appears
			var topRightRegion = new Rectangle(
				(int)(bitmap.Width * 0.60),  // Start at 60% from left
				0,
				(int)(bitmap.Width * 0.40),  // Take right 40%
				(int)(bitmap.Height * 0.18)  // Top 18%
			);
			
			string ocrText = ReadOcrInRegion(bitmap, topRightRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:- ");
			Logger.LogInfo($"[ExtractMap] Top-right OCR text: '{ocrText?.Replace("\n", " | ")}'");
			if (string.IsNullOrWhiteSpace(ocrText)) return null;
			
			// Log available maps for debugging
			var maps = ArcRaidersData.GetMaps();
			Logger.LogInfo($"[ExtractMap] Available maps: {string.Join(", ", maps.Take(10).Select(m => m.GetName()))}...");
			
			// Try to match against known maps first
			var map = MatchMapFromText(ocrText);
			if (map != null) {
				Logger.LogInfo($"[ExtractMap] Matched map: {map.GetName()}");
				return map;
			}
			
			Logger.LogInfo("[ExtractMap] No map match found");
			return null;
		} catch (Exception ex) {
			Logger.LogInfo($"ExtractMapNameFromInRaidView failed: {ex.Message}");
			return null;
		}
	}

	private bool TryAutoMapExtract(Bitmap bitmap, string sourceLabel, out RaidTheoryDataSource.RaidTheoryMap? map, bool bypassCooldown = false, bool updatePosition = true) {
		map = null;
		if (!bypassCooldown && (DateTime.UtcNow - _lastMapExtractUtc).TotalMilliseconds < ExtractCooldownMs) return false;
		_lastMapExtractUtc = DateTime.UtcNow;

		var rightPanelRegion = GetRightRegion(bitmap, 0.3, 0.4);
		string rightPanelText = ReadOcrInRegion(bitmap, rightPanelRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
		map = MatchMapFromText(rightPanelText);

		if (map == null) {
			var topRegion = GetTopRegion(bitmap, 0.2);
			string topText = ReadOcrInRegion(bitmap, topRegion, PageSegMode.Auto, "ABCDEFGHIJKLMNOPQRSTUVWXYZ");
			map = MatchMapFromText(topText);
		}

		if (map == null) return false;

		bool mapChanged = !string.Equals(_lastDetectedMapId, map.Id, StringComparison.OrdinalIgnoreCase);
		bool noPosition = MapOverlayManager.Instance.GetCurrentPosition() == null;
		if (updatePosition && (mapChanged || noPosition || bypassCooldown)) {
			MapOverlayManager.Instance.UpdatePosition(50, 50, map.Id, map.GetName(), map.Image);
			Logger.LogInfo($"{sourceLabel} map detected: {map.GetName()} (position set to center)");
		}

		_lastDetectedMapId = map.Id;
		return true;
	}

	private bool TryDetectPlayerPositionFromMapView(Bitmap bitmap, RaidTheoryDataSource.RaidTheoryMap map, out double xPercent, out double yPercent) {
		xPercent = 50;
		yPercent = 50;

		// The in-game map view has:
		// - Left quest panel: ~0-15% of screen (not part of map)
		// - Map area: ~15-88% of screen width (the actual map)
		// - Right legend: ~88-100% of screen (not part of map)
		// Vertically: ~8% (nav bar) to ~95%
		
		// Crop to just the map area
		double mapStartX = 0.15;  // Map starts here
		double mapEndX = 0.88;   // Map ends here (before legend)
		double mapStartY = 0.08;
		double mapEndY = 0.95;
		
		Rectangle mapRegion = new Rectangle(
			(int)(bitmap.Width * mapStartX),
			(int)(bitmap.Height * mapStartY),
			(int)(bitmap.Width * (mapEndX - mapStartX)),
			(int)(bitmap.Height * (mapEndY - mapStartY))
		);
		
		Logger.LogInfo($"[PosDetect] Map region: {mapRegion.X},{mapRegion.Y} {mapRegion.Width}x{mapRegion.Height} from {bitmap.Width}x{bitmap.Height}");
		
		if (mapRegion.Width < 50 || mapRegion.Height < 50) {
			Logger.LogInfo("[PosDetect] Map region too small");
			return false;
		}

		using Bitmap mapView = bitmap.Clone(mapRegion, PixelFormat.Format24bppRgb);
		
		Logger.LogInfo($"[PosDetect] Scanning for position on map: {map.Name}");
		
		// Primary method: OCR location names and match to known coordinates
		Logger.LogInfo("[PosDetect] Trying OCR location matching...");
		if (TryDetectPositionFromLocationNames(mapView, map.Id, out double nameX, out double nameY)) {
			xPercent = nameX;
			yPercent = nameY;
			Logger.LogInfo($"✓ Position from location name: {xPercent:0.1}%, {yPercent:0.1}%");
			return true;
		}
		Logger.LogInfo("[PosDetect] OCR location failed, trying cyan marker...");
		
		// Fallback: Try to find the cyan player marker
		if (TryFindPlayerMarker(mapView, out Point markerPos, out int pixelCount)) {
			// markerPos is relative to the cropped map region
			// Convert to percentage within the map area
			xPercent = (markerPos.X / (double)mapView.Width) * 100.0;
			yPercent = (markerPos.Y / (double)mapView.Height) * 100.0;
			xPercent = Math.Clamp(xPercent, 0, 100);
			yPercent = Math.Clamp(yPercent, 0, 100);
			Logger.LogInfo($"✓ Position from cyan marker: ({markerPos.X}, {markerPos.Y}) in {mapView.Width}x{mapView.Height} map = {xPercent:0.1}%, {yPercent:0.1}% [{pixelCount} pixels]");
			
			// Save debug image with cyan pixels highlighted (once per session)
			SaveCyanDebugImage(mapView, markerPos);
			
			return true;
		}
		
		Logger.LogDebug($"Cyan marker not found (only {pixelCount} pixels detected, need 15+)");
		return false;
	}

	private bool TryDetectPositionFromLocationNames(Bitmap mapView, string mapId, out double xPercent, out double yPercent) {
		xPercent = 50;
		yPercent = 50;
		
		try {
			// Get known locations for this map first
			var locations = GetMapLocations(mapId);
			if (locations.Count == 0) {
				Logger.LogDebug($"No location data for map: {mapId}");
				return false;
			}
			
			// Try multiple OCR modes to capture location text
			string ocrText = "";
			
			// Try Auto mode first (best for mixed text)
			string autoText = ReadOcrInRegion(mapView, new Rectangle(0, 0, mapView.Width, mapView.Height), 
				PageSegMode.Auto, null);
			if (!string.IsNullOrWhiteSpace(autoText)) {
				ocrText += autoText + " ";
			}
			
			// Try SparseText mode (for isolated words like location names)
			string sparseText = ReadOcrInRegion(mapView, new Rectangle(0, 0, mapView.Width, mapView.Height), 
				PageSegMode.SparseText, null);
			if (!string.IsNullOrWhiteSpace(sparseText)) {
				ocrText += sparseText + " ";
			}
			
			// Try SingleBlock mode
			string blockText = ReadOcrInRegion(mapView, new Rectangle(0, 0, mapView.Width, mapView.Height), 
				PageSegMode.SingleBlock, null);
			if (!string.IsNullOrWhiteSpace(blockText)) {
				ocrText += blockText;
			}
			
			// If still empty, try inverted colors (light text on dark background)
			if (string.IsNullOrWhiteSpace(ocrText)) {
				string invAuto = ReadOcrInRegionInverted(mapView, new Rectangle(0, 0, mapView.Width, mapView.Height),
					PageSegMode.Auto, null);
				if (!string.IsNullOrWhiteSpace(invAuto)) {
					ocrText += invAuto + " ";
				}
				
				string invSparse = ReadOcrInRegionInverted(mapView, new Rectangle(0, 0, mapView.Width, mapView.Height),
					PageSegMode.SparseText, null);
				if (!string.IsNullOrWhiteSpace(invSparse)) {
					ocrText += invSparse + " ";
				}
			}
			
			if (string.IsNullOrWhiteSpace(ocrText)) {
				Logger.LogDebug("No text detected on map for location matching (tried multiple OCR modes)");
				return false;
			}
			
			Logger.LogInfo($"Map OCR detected: {ocrText.Replace("\n", " | ").Substring(0, Math.Min(200, ocrText.Length))}");
			
			// Find the best matching location based on OCR text
			MapLocation? bestMatch = null;
			int bestScore = 0;
			
			foreach (var location in locations) {
				int score = CalculateLocationMatchScore(ocrText, location.Name);
				Logger.LogDebug($"  {location.Name}: score {score}");
				if (score > bestScore) {
					bestScore = score;
					bestMatch = location;
				}
			}
			
			// Lower threshold from 3 to 2 for better detection
			if (bestMatch != null && bestScore >= 2) {
				xPercent = bestMatch.Value.X;
				yPercent = bestMatch.Value.Y;
				Logger.LogInfo($"✓ Matched location: '{bestMatch.Value.Name}' at ({xPercent:0.1}%, {yPercent:0.1}%) [score: {bestScore}]");
				return true;
			}
			
			Logger.LogDebug($"No confident location match (best: {bestMatch?.Name ?? "none"}, score: {bestScore})");
			return false;
		} catch (Exception ex) {
			Logger.LogWarning($"Location name detection failed: {ex.Message}");
			return false;
		}
	}
	
	private struct MapLocation {
		public string Name;
		public double X;  // Percentage 0-100
		public double Y;  // Percentage 0-100
	}
	
	private class MapLocationCache {
		public string MapId { get; set; } = "";
		public DateTime GeneratedUtc { get; set; }
		public string? SourceImagePath { get; set; }
		public List<MapLocation> Locations { get; set; } = new();
	}

	private static readonly Dictionary<string, List<MapLocation>> AutoMapLocations = new(StringComparer.OrdinalIgnoreCase);
	private static readonly object AutoMapLocationsLock = new();
	private static readonly HashSet<string> MapLabelStopWords = new(StringComparer.OrdinalIgnoreCase) {
		"legend",
		"map",
		"locations"
	};
	
	/// <summary>
	/// Get known location coordinates for a map
	/// </summary>
	private List<MapLocation> GetMapLocations(string mapId) {
		if (TryGetAutoMapLocations(mapId, out var autoLocations)) {
			return autoLocations;
		}
		
		var locations = new List<MapLocation>();
		
		// Dam Battlegrounds locations
		if (mapId == "dam_battlegrounds") {
			locations.Add(new MapLocation { Name = "Assembly Workshops", X = 48.0, Y = 33.0 });
			locations.Add(new MapLocation { Name = "Assembly", X = 46.0, Y = 38.0 });
			locations.Add(new MapLocation { Name = "West Broken Bridge", X = 25.0, Y = 42.0 });
			locations.Add(new MapLocation { Name = "East Broken Bridge", X = 70.0, Y = 42.0 });
			locations.Add(new MapLocation { Name = "Control Room", X = 48.0, Y = 52.0 });
			locations.Add(new MapLocation { Name = "Turbine Hall", X = 48.0, Y = 60.0 });
			locations.Add(new MapLocation { Name = "Power Station", X = 35.0, Y = 65.0 });
			locations.Add(new MapLocation { Name = "Generator Room", X = 60.0, Y = 65.0 });
			locations.Add(new MapLocation { Name = "Lower Dam", X = 48.0, Y = 75.0 });
			locations.Add(new MapLocation { Name = "Dam Plaza", X = 50.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Maintenance", X = 40.0, Y = 55.0 });
			locations.Add(new MapLocation { Name = "Security", X = 55.0, Y = 50.0 });
		}
		// The Spaceport locations
		else if (mapId == "the_spaceport") {
			locations.Add(new MapLocation { Name = "Launch Pad", X = 50.0, Y = 30.0 });
			locations.Add(new MapLocation { Name = "Terminal", X = 45.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Hangar", X = 60.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Control Tower", X = 50.0, Y = 55.0 });
			locations.Add(new MapLocation { Name = "Cargo Bay", X = 35.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Fuel Depot", X = 70.0, Y = 35.0 });
			locations.Add(new MapLocation { Name = "Maintenance Bay", X = 40.0, Y = 65.0 });
			locations.Add(new MapLocation { Name = "Security Office", X = 55.0, Y = 60.0 });
			locations.Add(new MapLocation { Name = "Landing Zone", X = 30.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Spaceport Plaza", X = 50.0, Y = 50.0 });
		}
		// Buried City locations
		else if (mapId == "buried_city") {
			locations.Add(new MapLocation { Name = "Ancient Plaza", X = 50.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Excavation Site", X = 35.0, Y = 35.0 });
			locations.Add(new MapLocation { Name = "Ruins", X = 65.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Underground", X = 45.0, Y = 60.0 });
			locations.Add(new MapLocation { Name = "Temple", X = 55.0, Y = 30.0 });
			locations.Add(new MapLocation { Name = "Catacombs", X = 40.0, Y = 70.0 });
			locations.Add(new MapLocation { Name = "Research Camp", X = 30.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Artifact Chamber", X = 60.0, Y = 55.0 });
			locations.Add(new MapLocation { Name = "Old Market", X = 45.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "City Center", X = 50.0, Y = 50.0 });
		}
		// The Blue Gate locations
		else if (mapId == "the_blue_gate") {
			locations.Add(new MapLocation { Name = "Gate Plaza", X = 50.0, Y = 35.0 });
			locations.Add(new MapLocation { Name = "Checkpoint", X = 48.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Guard Tower", X = 60.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Barracks", X = 40.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Armory", X = 55.0, Y = 55.0 });
			locations.Add(new MapLocation { Name = "Command Center", X = 50.0, Y = 60.0 });
			locations.Add(new MapLocation { Name = "Supply Depot", X = 35.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Outer Wall", X = 70.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Inner Courtyard", X = 50.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Watch Post", X = 65.0, Y = 35.0 });
		}
		// Stella Montis Upper locations
		else if (mapId == "stella_montis_upper") {
			locations.Add(new MapLocation { Name = "Observatory", X = 50.0, Y = 25.0 });
			locations.Add(new MapLocation { Name = "Research Lab", X = 45.0, Y = 35.0 });
			locations.Add(new MapLocation { Name = "Upper Terrace", X = 55.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Residential", X = 40.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Communications", X = 60.0, Y = 35.0 });
			locations.Add(new MapLocation { Name = "Medical Bay", X = 35.0, Y = 50.0 });
			locations.Add(new MapLocation { Name = "Greenhouse", X = 65.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Administration", X = 50.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Stella Plaza", X = 48.0, Y = 55.0 });
		}
		// Stella Montis Lower locations
		else if (mapId == "stella_montis_lower") {
			locations.Add(new MapLocation { Name = "Loading Bay", X = 25.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Eastern Tunnel", X = 65.0, Y = 30.0 });
			locations.Add(new MapLocation { Name = "Seed Vault", X = 50.0, Y = 65.0 });
			locations.Add(new MapLocation { Name = "Storage", X = 35.0, Y = 45.0 });
			locations.Add(new MapLocation { Name = "Workshop", X = 60.0, Y = 40.0 });
			locations.Add(new MapLocation { Name = "Power Plant", X = 45.0, Y = 55.0 });
			locations.Add(new MapLocation { Name = "Utility Tunnels", X = 40.0, Y = 70.0 });
			locations.Add(new MapLocation { Name = "Engineering", X = 55.0, Y = 60.0 });
		}
		
		return locations;
	}

	private bool TryGetAutoMapLocations(string mapId, out List<MapLocation> locations) {
		lock (AutoMapLocationsLock) {
			if (AutoMapLocations.TryGetValue(mapId, out locations!)) {
				return locations.Count > 0;
			}
		}
		
		if (TryLoadMapLocationsCache(mapId, out locations)) {
			lock (AutoMapLocationsLock) {
				AutoMapLocations[mapId] = locations;
			}
			return locations.Count > 0;
		}
		
		if (TryGenerateMapLocationsFromLabeledMap(mapId, out locations)) {
			lock (AutoMapLocationsLock) {
				AutoMapLocations[mapId] = locations;
			}
			SaveMapLocationsCache(mapId, locations);
			return locations.Count > 0;
		}
		
		locations = new List<MapLocation>();
		return false;
	}

	private static string GetMapLocationCachePath(string mapId) {
		string dir = Path.Combine(RatConfig.Paths.CacheDir, "MapLocations");
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, $"{mapId}.json");
	}

	private static bool TryLoadMapLocationsCache(string mapId, out List<MapLocation> locations) {
		locations = new List<MapLocation>();
		try {
			string cachePath = GetMapLocationCachePath(mapId);
			if (!File.Exists(cachePath)) return false;
			string json = File.ReadAllText(cachePath);
			var cache = JsonSerializer.Deserialize<MapLocationCache>(json, new JsonSerializerOptions {
				PropertyNameCaseInsensitive = true
			});
			if (cache == null || cache.Locations == null || cache.Locations.Count == 0) return false;
			locations = FilterMapLocationsForMap(mapId, cache.Locations);
			Logger.LogInfo($"Loaded {locations.Count} map locations from cache for {mapId}");
			return true;
		} catch (Exception ex) {
			Logger.LogDebug($"Failed to load map location cache for {mapId}: {ex.Message}");
			return false;
		}
	}

	private static void SaveMapLocationsCache(string mapId, List<MapLocation> locations) {
		try {
			string cachePath = GetMapLocationCachePath(mapId);
			var cache = new MapLocationCache {
				MapId = mapId,
				GeneratedUtc = DateTime.UtcNow,
				Locations = locations
			};
			string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions {
				WriteIndented = true
			});
			File.WriteAllText(cachePath, json);
		} catch (Exception ex) {
			Logger.LogDebug($"Failed to save map location cache for {mapId}: {ex.Message}");
		}
	}

	private bool TryGenerateMapLocationsFromLabeledMap(string mapId, out List<MapLocation> locations) {
		locations = new List<MapLocation>();
		try {
			if (!RaidTheoryDataSource.IsDataAvailable()) {
				try {
					RaidTheoryDataSource.EnsureDataAsync().GetAwaiter().GetResult();
				} catch (Exception ex) {
					Logger.LogDebug($"Map location generation: data download failed: {ex.Message}");
				}
			}
			
			var maps = RaidTheoryDataSource.LoadMaps();
			var map = maps.FirstOrDefault(m => string.Equals(m.Id, mapId, StringComparison.OrdinalIgnoreCase));
			string? imagePath = GetLabeledMapImagePath(mapId, map);
			if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) {
				Logger.LogDebug($"Map location generation: labeled map image not found for {mapId}");
				return false;
			}
			
			using var bitmap = new Bitmap(imagePath);
			string? mapName = map?.GetName();
			string? normalizedMapName = string.IsNullOrWhiteSpace(mapName) ? null : NormalizeText(mapName);
			locations = ExtractMapLocationsFromImage(bitmap, normalizedMapName);
			locations = FilterMapLocationsForMap(mapId, locations);
			if (locations.Count == 0) {
				Logger.LogDebug($"Map location generation: no labels detected for {mapId}");
				return false;
			}
			
			Logger.LogInfo($"Generated {locations.Count} map locations from labeled map for {mapId}");
			return true;
		} catch (Exception ex) {
			Logger.LogWarning($"Failed to generate map locations for {mapId}: {ex.Message}");
			return false;
		}
	}

	private static string? GetLabeledMapImagePath(string mapId, RaidTheoryDataSource.RaidTheoryMap? map) {
		try {
			if (map != null && !string.IsNullOrWhiteSpace(map.Image)) {
				string dataImagePath = Path.Combine(RatConfig.Paths.Data, map.Image);
				if (File.Exists(dataImagePath)) return dataImagePath;
			}
			
			string cachedPath = Path.Combine(RatConfig.Paths.CacheDir, "RaidTheoryData", "arcraiders-data-main", "images", "maps", $"{mapId}.png");
			if (File.Exists(cachedPath)) return cachedPath;
		} catch {
			// Ignore
		}
		return null;
	}

	private static List<MapLocation> FilterMapLocationsForMap(string mapId, List<MapLocation> locations) {
		if (locations == null || locations.Count == 0) return locations ?? new List<MapLocation>();
		try {
			var map = RaidTheoryDataSource.LoadMaps()
				.FirstOrDefault(m => string.Equals(m.Id, mapId, StringComparison.OrdinalIgnoreCase));
			string? mapName = map?.GetName();
			string? normalizedMapName = string.IsNullOrWhiteSpace(mapName) ? null : NormalizeText(mapName);
			if (string.IsNullOrWhiteSpace(normalizedMapName)) return locations;
			return locations
				.Where(l => NormalizeText(l.Name) != normalizedMapName)
				.ToList();
		} catch {
			return locations;
		}
	}

	private List<MapLocation> ExtractMapLocationsFromImage(Bitmap source, string? normalizedMapName) {
		var results = new Dictionary<string, (MapLocation location, float confidence)>(StringComparer.OrdinalIgnoreCase);
		const int ocrScale = 2;
		
		using Bitmap pre = PreprocessForOcr(source);
		AddMapLocationsFromOcr(pre, PageSegMode.SparseText, ocrScale, source.Width, source.Height, normalizedMapName, results);
		AddMapLocationsFromOcr(pre, PageSegMode.Auto, ocrScale, source.Width, source.Height, normalizedMapName, results);
		
		return results.Values
			.OrderByDescending(v => v.confidence)
			.Select(v => v.location)
			.ToList();
	}

	private void AddMapLocationsFromOcr(
		Bitmap preprocessed,
		PageSegMode mode,
		int scale,
		int originalWidth,
		int originalHeight,
		string? normalizedMapName,
		Dictionary<string, (MapLocation location, float confidence)> results) {
		if (!_ocrInitialized) {
			EnsureTrainedData();
		}
		try {
			string trainedDataPath = RatConfig.Paths.TrainedData;
			if (!Directory.Exists(trainedDataPath) ||
			    !Directory.EnumerateFiles(trainedDataPath, "*.traineddata").Any()) {
				Logger.LogWarning("No traineddata found for OCR");
				return;
			}
			
			using var engine = new TesseractEngine(trainedDataPath, "eng", EngineMode.Default);
			engine.SetVariable("tessedit_char_whitelist", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 -'");
			engine.SetVariable("preserve_interword_spaces", "1");
			using var pix = Tesseract.PixConverter.ToPix(preprocessed);
			using var page = engine.Process(pix, mode);
			using var iter = page.GetIterator();
			if (iter == null) return;
			iter.Begin();
			
			do {
				string text = iter.GetText(PageIteratorLevel.TextLine) ?? "";
				float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
				if (conf < 40f) continue;
				if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect)) continue;
				
				string cleaned = CleanMapLabel(text);
				if (string.IsNullOrWhiteSpace(cleaned)) continue;
				string normalized = NormalizeText(cleaned);
				if (normalized.Length < 3 || MapLabelStopWords.Contains(normalized)) continue;
				if (!string.IsNullOrWhiteSpace(normalizedMapName) && normalized == normalizedMapName) continue;
				if (!Regex.IsMatch(cleaned, "[A-Za-z]")) continue;
				
				double centerX = (rect.X1 + rect.X2) / 2.0 / scale;
				double centerY = (rect.Y1 + rect.Y2) / 2.0 / scale;
				if (centerX <= 0 || centerY <= 0) continue;
				double xPercent = Math.Clamp(centerX / originalWidth * 100.0, 0, 100);
				double yPercent = Math.Clamp(centerY / originalHeight * 100.0, 0, 100);
				
				var location = new MapLocation { Name = cleaned, X = xPercent, Y = yPercent };
				if (results.TryGetValue(cleaned, out var existing)) {
					if (conf > existing.confidence) {
						results[cleaned] = (location, conf);
					}
				} else {
					results[cleaned] = (location, conf);
				}
			} while (iter.Next(PageIteratorLevel.TextLine));
		} catch (Exception ex) {
			Logger.LogDebug($"Map OCR labeling failed: {ex.Message}");
		}
	}

	private static string CleanMapLabel(string text) {
		if (string.IsNullOrWhiteSpace(text)) return "";
		string cleaned = Regex.Replace(text, @"[^A-Za-z0-9 '\-]", " ");
		cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
		return cleaned;
	}
	
	/// <summary>
	/// Calculate how well a location name matches the OCR text
	/// Returns a score based on word matches and substring matches
	/// </summary>
	private int CalculateLocationMatchScore(string ocrText, string locationName) {
		if (string.IsNullOrEmpty(ocrText) || string.IsNullOrEmpty(locationName)) return 0;
		
		ocrText = ocrText.ToLowerInvariant();
		locationName = locationName.ToLowerInvariant();
		
		int score = 0;
		
		// Full exact match (case-insensitive)
		if (ocrText.Contains(locationName)) {
			score += 10;
		}
		
		// Word-by-word matching
		string[] locationWords = locationName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		foreach (string word in locationWords) {
			if (word.Length < 2) continue;  // Skip single letters
			
			// Exact word match
			if (ocrText.Contains(word)) {
				score += word.Length;  // Longer words = higher score
			}
			// Partial match (at least 70% of word)
			else {
				int minLength = Math.Max(3, (int)(word.Length * 0.7));
				for (int i = 0; i <= word.Length - minLength; i++) {
					string substring = word.Substring(i, Math.Min(minLength, word.Length - i));
					if (ocrText.Contains(substring)) {
						score += substring.Length / 2;  // Half points for partial
						break;
					}
				}
			}
		}
		
		return score;
	}

	private static Rectangle GetCenterRegion(Bitmap bitmap, double widthRatio, double heightRatio) {
		int width = Math.Max(1, (int)(bitmap.Width * widthRatio));
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		int x = (bitmap.Width - width) / 2;
		int y = (bitmap.Height - height) / 2;
		return new Rectangle(x, y, width, height);
	}

	private static bool TryFindPlayerMarker(Bitmap mapView, out Point markerPos) {
		return TryFindPlayerMarker(mapView, out markerPos, out _);
	}
	
	private static bool TryFindPlayerMarker(Bitmap mapView, out Point markerPos, out int pixelCount) {
		markerPos = new Point(0, 0);
		pixelCount = 0;
		long sumX = 0;
		long sumY = 0;
		int width = mapView.Width;
		int height = mapView.Height;
		byte[] mask = new byte[width * height];
		
		// Track potential cyan colors we're NOT matching (for debugging)
		int almostCyanCount = 0;
		int sampleR = 0, sampleG = 0, sampleB = 0;

		// Use LockBits for faster pixel access
		Rectangle rect = new Rectangle(0, 0, mapView.Width, mapView.Height);
		BitmapData data = mapView.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
		
		try {
			int stride = data.Stride;
			byte[] pixels = new byte[stride * mapView.Height];
			System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
			
			// Scan every pixel for the cyan marker color
			for (int y = 0; y < height; y++) {
				int rowOffset = y * stride;
				for (int x = 0; x < width; x++) {
					int offset = rowOffset + x * 3;
					byte b = pixels[offset];
					byte g = pixels[offset + 1];
					byte r = pixels[offset + 2];
					
					if (IsPlayerMarkerColorFast(r, g, b)) {
						mask[(y * width) + x] = 1;
						sumX += x;
						sumY += y;
						pixelCount++;
					}
					// Track "almost cyan" colors that might be the marker (for debug)
					else if (g > 150 && b > 150 && r < 150 && (g > r + 30 || b > r + 30)) {
						almostCyanCount++;
						if (almostCyanCount == 1) { sampleR = r; sampleG = g; sampleB = b; }
					}
				}
			}
		} finally {
			mapView.UnlockBits(data);
		}

		// The player arrow is small - need at least 15 cyan pixels to be confident
		// Too low causes false positives from random UI elements
		if (pixelCount < 15) {
			Logger.LogInfo($"[CyanScan] Only {pixelCount} strict cyan. Almost-cyan: {almostCyanCount}, sample: RGB({sampleR},{sampleG},{sampleB})");
			return false;
		}
		
		Logger.LogInfo($"[CyanScan] Found {pixelCount} cyan pixels, centroid: ({sumX / pixelCount}, {sumY / pixelCount})");
		
		// Find densest cluster of cyan pixels (the player arrow is a tight grouping)
		// This filters out scattered UI icons and focuses on the actual marker
		var cyanPixels = new List<(int x, int y)>();
		for (int y = 0; y < height; y++) {
			for (int x = 0; x < width; x++) {
				if (mask[(y * width) + x] == 1) {
					cyanPixels.Add((x, y));
				}
			}
		}
		
		// Find pixel with highest local density (most neighbors within 25px radius)
		int bestDensity = 0;
		int bestIdx = 0;
		const int DENSITY_RADIUS = 25;
		
		for (int i = 0; i < cyanPixels.Count; i++) {
			var (px, py) = cyanPixels[i];
			int density = 0;
			for (int j = 0; j < cyanPixels.Count; j++) {
				if (i == j) continue;
				var (qx, qy) = cyanPixels[j];
				int dx = px - qx, dy = py - qy;
				if (dx * dx + dy * dy < DENSITY_RADIUS * DENSITY_RADIUS) {
					density++;
				}
			}
			if (density > bestDensity) {
				bestDensity = density;
				bestIdx = i;
			}
		}
		
		// Calculate cluster centroid (only pixels near the densest point)
		var (cx, cy) = cyanPixels[bestIdx];
		long clusterX = 0, clusterY = 0;
		int clusterCount = 0;
		foreach (var (px, py) in cyanPixels) {
			int dx = px - cx, dy = py - cy;
			if (dx * dx + dy * dy < DENSITY_RADIUS * DENSITY_RADIUS) {
				clusterX += px;
				clusterY += py;
				clusterCount++;
			}
		}
		
		if (clusterCount >= 10) {
			markerPos = new Point((int)(clusterX / clusterCount), (int)(clusterY / clusterCount));
			Logger.LogInfo($"[CyanScan] Cluster: {clusterCount} pixels around ({cx},{cy}), using centroid ({markerPos.X}, {markerPos.Y})");
			return true;
		}
		
		// First try multi-scale template matching for more accurate placement
		if (TryFindPlayerMarkerWithTemplate(mask, width, height, (int)(sumX / pixelCount), (int)(sumY / pixelCount), out Point templatePos)) {
			Logger.LogDebug($"Template match successful at ({templatePos.X}, {templatePos.Y})");
			markerPos = templatePos;
			return true;
		}
		
		// Fallback: centroid of cyan pixels
		markerPos = new Point((int)(sumX / pixelCount), (int)(sumY / pixelCount));
		Logger.LogDebug($"Using centroid fallback at ({markerPos.X}, {markerPos.Y})");
		return true;
	}

	private static bool TryFindPlayerMarkerWithTemplate(byte[] mask, int width, int height, int centerX, int centerY, out Point markerPos) {
		markerPos = new Point(0, 0);
		var baseTemplate = GetBaseMarkerTemplate();
		var scales = new[] { 0.7, 0.85, 1.0, 1.15, 1.3 };

		int searchRadius = Math.Max(25, Math.Min(width, height) / 6);
		int minX = Math.Max(0, centerX - searchRadius);
		int maxX = Math.Min(width - 1, centerX + searchRadius);
		int minY = Math.Max(0, centerY - searchRadius);
		int maxY = Math.Min(height - 1, centerY + searchRadius);

		double bestScore = 0.0;
		Point bestPos = new Point(0, 0);

		foreach (var scale in scales) {
			var template = ScaleTemplate(baseTemplate, scale);
			int tHeight = template.GetLength(0);
			int tWidth = template.GetLength(1);
			int tCount = CountTemplatePixels(template);
			if (tCount <= 0) continue;

			int startX = Math.Max(minX, 0);
			int startY = Math.Max(minY, 0);
			int endX = Math.Min(maxX - tWidth, width - tWidth);
			int endY = Math.Min(maxY - tHeight, height - tHeight);
			if (endX <= startX || endY <= startY) continue;

			for (int y = startY; y <= endY; y += 2) {
				int rowBase = y * width;
				for (int x = startX; x <= endX; x += 2) {
					int overlap = 0;
					for (int ty = 0; ty < tHeight; ty++) {
						int maskRow = rowBase + (ty * width) + x;
						for (int tx = 0; tx < tWidth; tx++) {
							if (!template[ty, tx]) continue;
							if (mask[maskRow + tx] == 1) overlap++;
						}
					}

					double score = overlap / (double)tCount;
					if (score > bestScore) {
						bestScore = score;
						bestPos = new Point(x + (tWidth / 2), y + (tHeight / 2));
					}
				}
			}
		}

		// Use template match if score is decent (35% overlap)
		if (bestScore >= 0.35) {
			Logger.LogDebug($"Template match score: {bestScore:0.00}");
			markerPos = bestPos;
			return true;
		}

		Logger.LogDebug($"Template match failed, best score: {bestScore:0.00}");
		return false;
	}

	private static bool[,] GetBaseMarkerTemplate() {
		// Simple arrow-like template (9x9) for the cyan player marker
		// 1s represent expected cyan pixels
		string[] rows = {
			"....1....",
			"...111...",
			"..11111..",
			".1111111.",
			"..11111..",
			"...111...",
			"....1....",
			"....1....",
			"....1...."
		};
		bool[,] template = new bool[rows.Length, rows[0].Length];
		for (int y = 0; y < rows.Length; y++) {
			for (int x = 0; x < rows[y].Length; x++) {
				template[y, x] = rows[y][x] == '1';
			}
		}
		return template;
	}

	private static bool[,] ScaleTemplate(bool[,] template, double scale) {
		int srcH = template.GetLength(0);
		int srcW = template.GetLength(1);
		int dstH = Math.Max(3, (int)Math.Round(srcH * scale));
		int dstW = Math.Max(3, (int)Math.Round(srcW * scale));
		bool[,] scaled = new bool[dstH, dstW];
		for (int y = 0; y < dstH; y++) {
			int srcY = (int)Math.Min(srcH - 1, Math.Round(y / scale));
			for (int x = 0; x < dstW; x++) {
				int srcX = (int)Math.Min(srcW - 1, Math.Round(x / scale));
				scaled[y, x] = template[srcY, srcX];
			}
		}
		return scaled;
	}

	private static int CountTemplatePixels(bool[,] template) {
		int count = 0;
		int h = template.GetLength(0);
		int w = template.GetLength(1);
		for (int y = 0; y < h; y++) {
			for (int x = 0; x < w; x++) {
				if (template[y, x]) count++;
			}
		}
		return count;
	}
	
	private static bool IsPlayerMarkerColorFast(byte r, byte g, byte b) {
		// The player marker in Arc Raiders is a bright cyan/teal arrow
		// Tightened thresholds to avoid false positives from UI elements
		
		// Primary cyan detection - the BRIGHT teal of the player arrow
		// Player arrow is a very saturated bright cyan: roughly RGB(0-100, 200-255, 200-255)
		if (r < 100 && g > 180 && b > 180) {
			// Must be clearly cyan (green and blue much higher than red)
			if (g > r + 80 && b > r + 80) {
				return true;
			}
		}
		
		// Bright cyan glow/highlight around arrow
		if (r < 130 && g > 200 && b > 210) {
			if (g > r + 70 && b > r + 70) {
				return true;
			}
		}
		
		return false;
	}

	private static bool IsPlayerMarkerColor(Color c) {
		return IsPlayerMarkerColorFast(c.R, c.G, c.B);
	}
	
	/// <summary>
	/// Save debug images for troubleshooting player marker detection
	/// </summary>
	private void SaveDebugMapImage(Bitmap bitmap, string suffix) {
		try {
			if ((DateTime.UtcNow - _lastDebugSaveUtc).TotalMilliseconds < DebugSaveCooldownMs) return;
			_lastDebugSaveUtc = DateTime.UtcNow;
			
			string debugDir = Path.Combine(RatConfig.Paths.Base, "debug");
			Directory.CreateDirectory(debugDir);
			
			// Save full screenshot
			string timestamp = DateTime.Now.ToString("HHmmss");
			string fullPath = Path.Combine(debugDir, $"map_{suffix}_{timestamp}.png");
			bitmap.Save(fullPath, System.Drawing.Imaging.ImageFormat.Png);
			
			// Also save the cropped map region with cyan pixel highlighting
			Rectangle mapRegion = new Rectangle(
				(int)(bitmap.Width * 0.28),
				(int)(bitmap.Height * 0.08),
				(int)(bitmap.Width * 0.72),
				(int)(bitmap.Height * 0.87)
			);
			
			using Bitmap mapView = bitmap.Clone(mapRegion, PixelFormat.Format24bppRgb);
			
			// Highlight detected cyan pixels in red for debugging
			for (int y = 0; y < mapView.Height; y++) {
				for (int x = 0; x < mapView.Width; x++) {
					Color c = mapView.GetPixel(x, y);
					if (IsPlayerMarkerColorFast(c.R, c.G, c.B)) {
						mapView.SetPixel(x, y, Color.Red);
					}
				}
			}
			
			string cropPath = Path.Combine(debugDir, $"map_crop_{suffix}_{timestamp}.png");
			mapView.Save(cropPath, System.Drawing.Imaging.ImageFormat.Png);
			
			Logger.LogDebug($"Debug images saved to {debugDir}");
		} catch (Exception ex) {
			Logger.LogDebug($"Failed to save debug image: {ex.Message}");
		}
	}
	
	private static DateTime _lastCyanDebugSave = DateTime.MinValue;
	
	private void SaveCyanDebugImage(Bitmap mapView, Point detectedPos) {
		try {
			// Only save once per 30 seconds to avoid spamming
			if ((DateTime.UtcNow - _lastCyanDebugSave).TotalSeconds < 30) return;
			_lastCyanDebugSave = DateTime.UtcNow;
			
			string debugDir = Path.Combine(RatConfig.Paths.Base, "debug");
			Directory.CreateDirectory(debugDir);
			
			// Clone the map view and highlight detected cyan pixels in red
			using Bitmap debugImg = new Bitmap(mapView.Width, mapView.Height);
			using var g = Graphics.FromImage(debugImg);
			g.DrawImage(mapView, 0, 0);
			
			// Highlight all cyan pixels in bright magenta
			for (int y = 0; y < mapView.Height; y++) {
				for (int x = 0; x < mapView.Width; x++) {
					Color c = mapView.GetPixel(x, y);
					if (IsPlayerMarkerColorFast(c.R, c.G, c.B)) {
						debugImg.SetPixel(x, y, Color.Magenta);
					}
				}
			}
			
			// Draw a bright green cross at the detected position
			using var pen = new Pen(Color.Lime, 3);
			g.DrawLine(pen, detectedPos.X - 20, detectedPos.Y, detectedPos.X + 20, detectedPos.Y);
			g.DrawLine(pen, detectedPos.X, detectedPos.Y - 20, detectedPos.X, detectedPos.Y + 20);
			
			string timestamp = DateTime.Now.ToString("HHmmss");
			string path = Path.Combine(debugDir, $"cyan_debug_{timestamp}.png");
			debugImg.Save(path, System.Drawing.Imaging.ImageFormat.Png);
			Logger.LogInfo($"[Debug] Cyan detection image saved to: {path}");
		} catch (Exception ex) {
			Logger.LogDebug($"Failed to save cyan debug image: {ex.Message}");
		}
	}

	private Bitmap? LoadMapImage(RaidTheoryDataSource.RaidTheoryMap map) {
		try {
			string? imagePath = map.Image;
			if (string.IsNullOrWhiteSpace(imagePath)) return null;

			if (Uri.TryCreate(imagePath, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) {
				string fileName = Path.GetFileName(uri.LocalPath);
				if (!string.IsNullOrWhiteSpace(fileName)) {
					string localCandidate = Path.Combine(RatConfig.Paths.Data, "maps", fileName);
					if (File.Exists(localCandidate)) {
						return new Bitmap(localCandidate);
					}

					byte[] data = HttpClient.GetByteArrayAsync(uri).GetAwaiter().GetResult();
					Directory.CreateDirectory(Path.GetDirectoryName(localCandidate) ?? RatConfig.Paths.Data);
					File.WriteAllBytes(localCandidate, data);
					return new Bitmap(localCandidate);
				}
				return null;
			}

			if (File.Exists(imagePath)) {
				return new Bitmap(imagePath);
			}
		} catch (Exception ex) {
			Logger.LogDebug($"LoadMapImage failed: {ex.Message}");
		}
		return null;
	}

	private static bool TryMatchMapRegion(Bitmap mapImage, Bitmap mapView, Point markerPos, out double xPercent, out double yPercent) {
		xPercent = 50;
		yPercent = 50;

		using Bitmap mapSmall = ResizeBitmap(mapImage, 512);
		using Bitmap viewSmallBase = ResizeBitmap(mapView, 256);
		Point markerSmall = new(
			(int)(markerPos.X * (viewSmallBase.Width / (double)mapView.Width)),
			(int)(markerPos.Y * (viewSmallBase.Height / (double)mapView.Height))
		);

		byte[] mapGray = ToGrayscale(mapSmall, out int mapW, out int mapH);

		double bestScore = double.MaxValue;
		int bestX = 0;
		int bestY = 0;
		double bestScale = 1.0;

		for (double scale = 0.6; scale <= 1.4; scale += 0.1) {
			int viewW = Math.Max(20, (int)(viewSmallBase.Width * scale));
			int viewH = Math.Max(20, (int)(viewSmallBase.Height * scale));
			if (viewW >= mapW || viewH >= mapH) continue;

			using Bitmap viewScaled = ResizeBitmap(viewSmallBase, viewW, viewH);
			byte[] viewGray = ToGrayscale(viewScaled, out int vw, out int vh);

			int step = 4;
			int sampleStep = 2;
			for (int y = 0; y <= mapH - vh; y += step) {
				for (int x = 0; x <= mapW - vw; x += step) {
					long diff = 0;
					for (int yy = 0; yy < vh; yy += sampleStep) {
						int mapRow = (y + yy) * mapW;
						int viewRow = yy * vw;
						for (int xx = 0; xx < vw; xx += sampleStep) {
							diff += Math.Abs(mapGray[mapRow + x + xx] - viewGray[viewRow + xx]);
						}
						if (diff >= bestScore) break;
					}
					if (diff < bestScore) {
						bestScore = diff;
						bestX = x;
						bestY = y;
						bestScale = scale;
					}
				}
			}
		}

		if (bestScore == double.MaxValue) return false;

		int markerX = bestX + (int)(markerSmall.X * bestScale);
		int markerY = bestY + (int)(markerSmall.Y * bestScale);
		markerX = Math.Clamp(markerX, 0, mapW - 1);
		markerY = Math.Clamp(markerY, 0, mapH - 1);

		xPercent = markerX / (double)mapW * 100.0;
		yPercent = markerY / (double)mapH * 100.0;
		return true;
	}

	private static Bitmap ResizeBitmap(Bitmap source, int targetWidth) {
		int targetHeight = Math.Max(1, (int)(source.Height * (targetWidth / (double)source.Width)));
		return ResizeBitmap(source, targetWidth, targetHeight);
	}

	private static Bitmap ResizeBitmap(Bitmap source, int targetWidth, int targetHeight) {
		Bitmap resized = new(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
		using Graphics gfx = Graphics.FromImage(resized);
		gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
		gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
		gfx.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
		gfx.DrawImage(source, 0, 0, targetWidth, targetHeight);
		return resized;
	}

	private static byte[] ToGrayscale(Bitmap bitmap, out int width, out int height) {
		width = bitmap.Width;
		height = bitmap.Height;
		Rectangle rect = new(0, 0, width, height);
		BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
		try {
			int stride = data.Stride;
			int bytes = stride * height;
			byte[] rgb = new byte[bytes];
			System.Runtime.InteropServices.Marshal.Copy(data.Scan0, rgb, 0, bytes);
			byte[] gray = new byte[width * height];
			for (int y = 0; y < height; y++) {
				int row = y * stride;
				int outRow = y * width;
				for (int x = 0; x < width; x++) {
					int offset = row + x * 3;
					byte b = rgb[offset];
					byte g = rgb[offset + 1];
					byte r = rgb[offset + 2];
					gray[outRow + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
				}
			}
			return gray;
		} finally {
			bitmap.UnlockBits(data);
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
	
	private static string ReadOcrInRegionInverted(Bitmap bitmap, Rectangle region, PageSegMode mode, string? whitelist) {
		if (!_ocrInitialized) {
			EnsureTrainedData();
		}
		region = Rectangle.Intersect(region, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
		if (region.Width < 10 || region.Height < 10) return "";

		using Bitmap crop = bitmap.Clone(region, PixelFormat.Format24bppRgb);
		InvertBitmap(crop);
		using Bitmap pre = PreprocessForOcr(crop);
		return PerformOcr(pre, mode, whitelist);
	}

	private static void InvertBitmap(Bitmap bitmap) {
		Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
		BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
		try {
			int stride = data.Stride;
			int bytes = Math.Abs(stride) * bitmap.Height;
			byte[] buffer = new byte[bytes];
			System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);
			for (int i = 0; i < bytes; i++) {
				buffer[i] = (byte)(255 - buffer[i]);
			}
			System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, bytes);
		} finally {
			bitmap.UnlockBits(data);
		}
	}

	private static Rectangle GetTopRegion(Bitmap bitmap, double heightRatio) {
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(0, 0, bitmap.Width, height);
	}

	private static Rectangle GetRightRegion(Bitmap bitmap, double widthRatio, double heightRatio) {
		int width = Math.Max(1, (int)(bitmap.Width * widthRatio));
		int height = Math.Max(1, (int)(bitmap.Height * heightRatio));
		return new Rectangle(bitmap.Width - width, 0, width, height);
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
		
		// Match Roman numerals without word boundaries (I can appear alone)
		// Pattern matches: IV, IX, VIII, VII, VI, V, IV, III, II, I, and digits
		// Order: longer patterns first to avoid partial matches (IV before I)
		var matches = Regex.Matches(normalized, @"(?:IV|IX|VIII|VII|VI|V|III|II|I|[0-9]+)", RegexOptions.IgnoreCase);
		
		foreach (Match match in matches) {
			string token = match.Value.ToUpperInvariant().Trim();
			if (string.IsNullOrWhiteSpace(token)) continue;
			
			// Try parsing as digit first
			if (int.TryParse(token, out int num)) {
				yield return Math.Clamp(num, 0, 10);
				continue;
			}
			
			// Parse as Roman numeral
			int result = token switch {
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
			
			if (result > 0) yield return result;
		}
	}

	private static string? TryMatchModuleIdFromText(string? ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) return null;
		string normalized = NormalizeText(ocrText);
		if (string.IsNullOrWhiteSpace(normalized)) return null;

		var modules = ArcRaidersData.GetHideoutModules();
		foreach (var module in modules) {
			string name = module.GetName();
			if (string.IsNullOrWhiteSpace(name)) continue;
			string normName = NormalizeText(name);
			if (normName.Length < 3) continue;
			if (normalized.Contains(normName)) return module.Id;

			var tokens = normName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Where(t => t.Length >= 3)
				.ToList();
			if (tokens.Count == 0) continue;
			bool allTokensFound = tokens.All(t => normalized.Contains(t));
			if (allTokensFound) return module.Id;
		}

		return null;
	}

	private bool TryApplyStableWorkbenchLevels(Dictionary<string, int> levels, bool replace) {
		if (levels.Count == 0) return false;
		string signature = string.Join("|", levels
			.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
			.Select(kvp => $"{kvp.Key}:{kvp.Value}"));

		if (string.Equals(_pendingWorkbenchSignature, signature, StringComparison.Ordinal)) {
			_pendingWorkbenchHits++;
			if (_pendingWorkbenchHits >= 2) {
				_pendingWorkbenchSignature = null;
				_pendingWorkbenchHits = 0;
				PlayerStateManager.SetWorkbenchLevels(levels, replace);
				return true;
			}
			return false;
		}

		_pendingWorkbenchSignature = signature;
		_pendingWorkbenchHits = 1;
		return false;
	}

	private static readonly HashSet<string> MapNameStopWords = new(StringComparer.OrdinalIgnoreCase) {
		"the", "of", "at", "in", "on", "a", "an"
	};

	private static RaidTheoryDataSource.RaidTheoryMap? MatchMapFromText(string ocrText) {
		if (string.IsNullOrWhiteSpace(ocrText)) return null;
		string normalized = NormalizeText(ocrText);
		string normalizedNoSpace = normalized.Replace(" ", "");
		var maps = ArcRaidersData.GetMaps();
		
		// 1. Exact string containment (Best case)
		foreach (var map in maps) {
			string name = map.GetName();
			if (string.IsNullOrWhiteSpace(name)) continue;
			string normName = NormalizeText(name);
			string normNameNoSpace = normName.Replace(" ", "");
			if (normName.Length < 3) continue;
			if (normalized.Contains(normName) || normalizedNoSpace.Contains(normNameNoSpace)) return map;
		}

		// 1b. Token containment (handles partial OCR like "SPACEPORT" for "The Spaceport")
		// Only require significant tokens (ignore stopwords like "the", "of")
		foreach (var map in maps) {
			string name = map.GetName();
			if (string.IsNullOrWhiteSpace(name)) continue;
			string normName = NormalizeText(name);

			// Get significant tokens (4+ chars and not stopwords)
			var significantTokens = normName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Where(t => t.Length >= 4 && !MapNameStopWords.Contains(t))
				.ToList();

			if (significantTokens.Count == 0) continue;
			
			// Match if ALL significant tokens are found
			bool allSignificantFound = significantTokens.All(t => normalized.Contains(t) || normalizedNoSpace.Contains(t));
			if (allSignificantFound) {
				Logger.LogInfo($"Token map match: '{name}' via tokens [{string.Join(", ", significantTokens)}]");
				return map;
			}
		}

		// 2. Fuzzy matching (Fallback)
		// Only attempt if the text length is somewhat similar to the map name, 
		// verifying that we captured just the map title or close to it.
		foreach (var map in maps) {
			string name = map.GetName();
			string normName = NormalizeText(name);
			string normNameNoSpace = normName.Replace(" ", "");
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

			if (Math.Abs(normalizedNoSpace.Length - normNameNoSpace.Length) <= 5) {
				int dist = normalizedNoSpace.LevenshteinDistance(normNameNoSpace);
				int threshold = Math.Max(3, normNameNoSpace.Length / 5);
				if (dist <= threshold) {
					Logger.LogInfo($"Fuzzy map match (no-space): '{normalizedNoSpace}' ~= '{normNameNoSpace}' (Dist: {dist})");
					return map;
				}
			}
		}

		// NOTE: Removed OCR fallback that created fake maps from garbage text
		// We should only match against known maps from the data source
		// This prevents "Meic Imen Cin" type errors from OCR misreads

		return null;
	}

	private static string? ExtractMapNameFromOcr(string normalized) {
		if (string.IsNullOrWhiteSpace(normalized)) return null;
		var words = normalized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		string best = "";
		string current = "";

		foreach (var word in words) {
			if (word.Length < 3 || OcrMapStopWords.Contains(word)) {
				if (current.Length > best.Length) best = current;
				current = "";
				continue;
			}

			current = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
			if (current.Length > best.Length) best = current;
		}

		if (current.Length > best.Length) best = current;
		if (best.Length < 4) return null;
		return best.Trim();
	}

	private static string SlugifyMapName(string value) {
		string slug = value.ToLowerInvariant();
		slug = Regex.Replace(slug, @"[^a-z0-9]+", "-");
		slug = Regex.Replace(slug, @"-+", "-").Trim('-');
		return string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
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
