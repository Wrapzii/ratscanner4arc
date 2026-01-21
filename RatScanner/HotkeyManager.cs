using RatScanner.View;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
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
		nameof(CloseInteractableOverlayHotkey))
	]
	internal void RegisterHotkeys() {
		// Unregister hotkeys to prevent multiple listeners for the same hotkey
		UnregisterHotkeys();

		// Disable plain hover/click tooltip scan; use configured TooltipScan hotkey instead (Alt + Click)
		NameScanHotkey = new ActiveHotkey(new Hotkey(), OnTooltipScanHotkey, ref TooltipScan.Enable);
		TooltipScanHotkey = new ActiveHotkey(TooltipScan.Hotkey, OnTooltipScanHotkey, ref TooltipScan.Enable);
		OpenInteractableOverlayHotkey = new ActiveHotkey(OverlayC.Search.Hotkey, OnOpenInteractableOverlayHotkey, ref OverlayC.Search.Enable);
		CloseInteractableOverlayHotkey = new ActiveHotkey(new Hotkey(new[] { Key.Escape }), OnCloseInteractableOverlayHotkey);
	}

	/// <summary>
	/// Unregister hotkeys
	/// </summary>
	internal void UnregisterHotkeys() {
		NameScanHotkey?.Dispose();
		TooltipScanHotkey?.Dispose();
		OpenInteractableOverlayHotkey?.Dispose();
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
}
