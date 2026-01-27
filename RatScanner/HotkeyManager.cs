using RatScanner.View;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using static RatScanner.RatConfig;
using OverlayC = RatScanner.RatConfig.Overlay;

namespace RatScanner;

internal class HotkeyManager {
	private long _last_mouse_click = 0;
	private DateTime _lastMapOpenKeyUtc = DateTime.MinValue;

	internal ActiveHotkey NameScanHotkey;
	internal ActiveHotkey TooltipScanHotkey;
	internal ActiveHotkey OpenInteractableOverlayHotkey;
	internal ActiveHotkey CloseInteractableOverlayHotkey;
	internal ActiveHotkey MapCalibrateHotkey;

	private CancellationTokenSource? _calibrationHoldCts;

	internal HotkeyManager() {
		UserActivityHelper.Start(true, true);
		RegisterHotkeys();
	}

	~HotkeyManager() {
		UnregisterHotkeys();
		UserActivityHelper.Stop(true, true, false);
	}

	/// <summary>
	/// Register hotkeys so the event handlers receive hotkey presses
	/// </summary>
	/// <remarks>
	/// Called by the constructor
	/// </remarks>
	[MemberNotNull(
		nameof(NameScanHotkey),
		nameof(TooltipScanHotkey),
		nameof(OpenInteractableOverlayHotkey),
		nameof(CloseInteractableOverlayHotkey),
		nameof(MapCalibrateHotkey))
	]
	internal void RegisterHotkeys() {
		// Unregister hotkeys to prevent multiple listeners for the same hotkey
		UnregisterHotkeys();

		// Disable plain hover/click tooltip scan; use configured TooltipScan hotkey instead (Alt + Click)
		NameScanHotkey = new ActiveHotkey(new Hotkey(), OnTooltipScanHotkey, ref TooltipScan.Enable);
		TooltipScanHotkey = new ActiveHotkey(TooltipScan.Hotkey, OnTooltipScanHotkey, ref TooltipScan.Enable);
		OpenInteractableOverlayHotkey = new ActiveHotkey(OverlayC.Search.Hotkey, OnOpenInteractableOverlayHotkey, ref OverlayC.Search.Enable);
		CloseInteractableOverlayHotkey = new ActiveHotkey(new Hotkey(new[] { Key.Escape }), OnCloseInteractableOverlayHotkey);
		MapCalibrateHotkey = new ActiveHotkey(RatConfig.Map.CalibrateHotkey, OnMapCalibrateHotkey);

		// Manual hooks for Hold-to-Calibrate logic
		UserActivityHelper.OnKeyboardKeyDown += OnCalibrationInternalKeyDown;
		UserActivityHelper.OnKeyboardKeyUp += OnCalibrationInternalKeyUp;
		UserActivityHelper.OnKeyboardKeyDown += OnMapOpenInternalKeyDown;
	}

	/// <summary>
	/// Unregister hotkeys
	/// </summary>
	internal void UnregisterHotkeys() {
		NameScanHotkey?.Dispose();
		TooltipScanHotkey?.Dispose();
		OpenInteractableOverlayHotkey?.Dispose();
		MapCalibrateHotkey?.Dispose();

		UserActivityHelper.OnKeyboardKeyDown -= OnCalibrationInternalKeyDown;
		UserActivityHelper.OnKeyboardKeyUp -= OnCalibrationInternalKeyUp;
		UserActivityHelper.OnKeyboardKeyDown -= OnMapOpenInternalKeyDown;
		CancelCalibrationHold();
	}

	private void CancelCalibrationHold() {
		_calibrationHoldCts?.Cancel();
		_calibrationHoldCts?.Dispose();
		_calibrationHoldCts = null;
	}

	private void OnCalibrationInternalKeyDown(object? sender, KeyDownEventArgs e) {
		// Only proceed if Hold is enabled and we aren't already holding
		if (!RatConfig.Map.UseHoldToCalibrate || _calibrationHoldCts != null) return;
		
		// Check if the pressed key matches the configured hold key
		var holdKey = RatConfig.Map.CalibrateHoldHotkey.KeyboardKeys.FirstOrDefault();
		if (holdKey == Key.None) return;

		// Handle Alt key specifically as it can appear as System or LeftAlt/RightAlt
		bool isMatch = e.Key == holdKey;
		if (!isMatch && (holdKey == Key.LeftAlt || holdKey == Key.RightAlt)) {
			isMatch = e.Key == Key.System || e.Key == Key.LeftAlt || e.Key == Key.RightAlt;
		}

		if (isMatch) {
			_calibrationHoldCts = new CancellationTokenSource();
			var token = _calibrationHoldCts.Token;
            
            // Log start of hold
			Logger.LogDebug($"[Hold] Started hold for {e.Key} (Timeout: {RatConfig.Map.CalibrateHoldDurationMs}ms)");

			Task.Run(async () => {
				try {
					int duration = RatConfig.Map.CalibrateHoldDurationMs;
					int elapsed = 0;
					const int stepMs = 50;
					while (elapsed < duration && !token.IsCancellationRequested) {
						await Task.Delay(stepMs).ConfigureAwait(false);
						elapsed += stepMs;
					}
					
					if (!token.IsCancellationRequested) {
						Logger.LogInfo("[Hold] Calibration hold complete. Triggering...");
						// Use dispatcher just in case, though mostly thread safe
						Application.Current.Dispatcher.Invoke(() => {
						    _ = RatScannerMain.Instance.StateDetectionManager.RunManualMapCalibration();
						});
						
						// play a sound or notification?
					}
				} finally {
					// Reset CTS if we finished (successful or valid cancel) 
					if (_calibrationHoldCts?.Token == token) {
						_calibrationHoldCts = null;
					}
				}
			}, token);
		}
	}

	private void OnCalibrationInternalKeyUp(object? sender, KeyUpEventArgs e) {
		if (_calibrationHoldCts == null) return;
		
		var holdKey = RatConfig.Map.CalibrateHoldHotkey.KeyboardKeys.FirstOrDefault();
		
		bool isMatch = e.Key == holdKey;
		if (!isMatch && (holdKey == Key.LeftAlt || holdKey == Key.RightAlt)) {
			isMatch = e.Key == Key.System || e.Key == Key.LeftAlt || e.Key == Key.RightAlt;
		}

		if (isMatch) {
			Logger.LogDebug($"[Hold] Key {e.Key} released. Cancelling hold.");
			CancelCalibrationHold();
		}
	}

	private void OnMapOpenInternalKeyDown(object? sender, KeyDownEventArgs e) {
		if (e.Device != Device.Keyboard) return;
		var mapKeys = RatConfig.Map.MapOpenHotkey.KeyboardKeys;
		if (mapKeys == null || mapKeys.Count == 0) return;

		// Only trigger when the pressed key is part of the hotkey and all keys are down
		if (!mapKeys.Contains(e.Key)) return;
		foreach (var key in mapKeys) {
			if (!UserActivityHelper.IsKeyDown(key)) return;
		}

		// Debounce repeated keydown events
		if ((DateTime.UtcNow - _lastMapOpenKeyUtc).TotalMilliseconds < 500) return;
		_lastMapOpenKeyUtc = DateTime.UtcNow;
		if (RatConfig.LogDebug) {
			Logger.LogDebug($"Map hotkey detected: {string.Join("+", mapKeys)} (inRaid={PlayerStateManager.GetState().IsInRaid})");
		}
		RatScannerMain.Instance.StateDetectionManager.RequestMapCaptureFromHotkey();
	}

	private static void Wrap<T>(Func<T> func) {
		try {
			func();
		} catch (Exception e) {
			Logger.LogError(e.Message, e);
		}
	}

	private static void Wrap(Action action) {
		try {
			action();
		} catch (Exception e) {
			Logger.LogError(e.Message, e);
		}
	}

	private void OnNameScanHotkey(object? sender, KeyUpEventArgs e) {
		Wrap(() => {
			// Avoid double-trigger when modifier hotkeys are used (e.g., Alt+Left)
			if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) return;
			RatScannerMain.Instance.NameScan(UserActivityHelper.GetMousePosition());
			if (_last_mouse_click + 500 < DateTimeOffset.Now.ToUnixTimeMilliseconds() && NameScan.EnableAuto) {
				Thread.Sleep(200);  // wait for double click and ui
				RatScannerMain.Instance.NameScanScreen();
				_last_mouse_click = DateTimeOffset.Now.ToUnixTimeMilliseconds();
			}
		});
	}

	private void OnTooltipScanHotkey(object? sender, KeyUpEventArgs e) {
		Wrap(() => RatScannerMain.Instance.TooltipScan(UserActivityHelper.GetMousePosition()));
	}

	private void OnOpenInteractableOverlayHotkey(object? sender, KeyUpEventArgs e) {
		Wrap(() => Application.Current.Dispatcher.Invoke(() => Wrap(() => BlazorUI.BlazorInteractableOverlay.ShowOverlay())));
	}

	private void OnCloseInteractableOverlayHotkey(object? sender, KeyUpEventArgs e) {
		Wrap(() => Application.Current.Dispatcher.Invoke(() => Wrap(() => BlazorUI.BlazorInteractableOverlay.HideOverlay())));
	}

	private void OnMapCalibrateHotkey(object? sender, KeyUpEventArgs e) {
		Wrap(() => {
			_ = Task.Run(async () => {
				await RatScannerMain.Instance.StateDetectionManager.RunManualMapCalibration();
			});
		});
	}
}
