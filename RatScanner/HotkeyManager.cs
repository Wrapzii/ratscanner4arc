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
		// We only support single-key hold logic for stability
		var holdKey = RatConfig.Map.CalibrateHoldHotkey.KeyboardKeys.FirstOrDefault();
		if (holdKey == Key.None) return;

		if (e.Key == holdKey) {
			_calibrationHoldCts = new CancellationTokenSource();
			var token = _calibrationHoldCts.Token;

			Task.Run(async () => {
				try {
					int duration = RatConfig.Map.CalibrateHoldDurationMs;
					Logger.LogDebug($"Hold {e.Key} for {duration}ms to calibrate...");
					await Task.Delay(duration, token);
					
					if (!token.IsCancellationRequested) {
						Logger.LogInfo("Calibration hold complete. Triggering...");
						await RatScannerMain.Instance.StateDetectionManager.RunManualMapCalibration();
					}
				} catch (TaskCanceledException) {
					// Expected when key is released early
					Logger.LogDebug("Calibration hold cancelled.");
				} finally {
					// Reset CTS if we finished (successful or valid cancel) 
					// But we need to be careful not to null out a NEW cts if this was a race, 
					// though single threading of UI events usually prevents this.
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
		if (e.Key == holdKey) {
			CancelCalibrationHold();
		}
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
