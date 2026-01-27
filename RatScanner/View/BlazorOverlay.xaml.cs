using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace RatScanner.View;

/// <summary>
/// Interaction logic for BlazorOverlay.xaml
/// </summary>
public partial class BlazorOverlay : Window {
	public BlazorOverlay(ServiceProvider serviceProvider) {
		Resources.Add("services", serviceProvider);

		InitializeComponent();
	}

	private void BlazorOverlay_Loaded(object? sender, RoutedEventArgs e) {
		blazorOverlayWebView.WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
		SetSize();
		SetWindowStyle();
		blazorOverlayWebView.WebView.NavigationCompleted += WebView_Loaded;
		blazorOverlayWebView.WebView.CoreWebView2InitializationCompleted += CoreWebView_Loaded;
	}

	private void SetSize() {
		System.Collections.Generic.IEnumerable<System.Drawing.Rectangle> bounds = Screen.AllScreens.Select(screen => screen.Bounds);
		int left = 0;
		int top = 0;
		int right = 0;
		int bottom = 0;
		foreach (System.Drawing.Rectangle bound in bounds) {
			if (bound.Left < left) left = bound.Left;
			if (bound.Top < top) top = bound.Top;
			if (bound.Right > right) right = bound.Right;
			if (bound.Bottom > bottom) bottom = bound.Bottom;
		}

		nint handle = new WindowInteropHelper(this).Handle;
		const int HWND_TOPMOST = -1;
		const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
		NativeMethods.SetWindowPos(handle, HWND_TOPMOST, left, top, right - left, bottom - top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
	}

	private void SetWindowStyle() {
		const int gwlExStyle = -20; // GWL_EXSTYLE
		const uint wsExToolWindow = 0x00000080; // WS_EX_TOOLWINDOW
		const uint wsExTransparent = 0x00000020; // WS_EX_TRANSPARENT
		const uint wsExNoActivate = 0x08000000; // WS_EX_NOACTIVATE
		const uint wdaExcludeFromCapture = 0x00000011; // WDA_EXCLUDEFROMCAPTURE
		const uint wdaNone = 0x00000000; // WDA_NONE

		nint handle = new WindowInteropHelper(this).Handle;
		// Set WS_EX_TRANSPARENT to make the window click-through
		NativeMethods.SetWindowLongPtr(handle, gwlExStyle, NativeMethods.GetWindowLongPtr(handle, gwlExStyle) | (nint)wsExToolWindow | (nint)wsExTransparent | (nint)wsExNoActivate);
		// Control whether overlay appears in screen capture
		uint affinity = RatConfig.Overlay.ExcludeFromCapture ? wdaExcludeFromCapture : wdaNone;
		if (!NativeMethods.SetWindowDisplayAffinity(handle, affinity) && RatConfig.Overlay.ExcludeFromCapture) {
			RatScanner.Logger.LogWarning($"Failed to exclude overlay from capture (error {Marshal.GetLastWin32Error()})");
		}
	}

	private void WebView_Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
		// If we are running in a development/debugger mode, open dev tools to help out
		if (Debugger.IsAttached) blazorOverlayWebView.WebView.CoreWebView2.OpenDevToolsWindow();
	}

	private void CoreWebView_Loaded(object? sender, CoreWebView2InitializationCompletedEventArgs e) {
		string dataPath = Path.Combine(AppContext.BaseDirectory, "Data");
		Directory.CreateDirectory(dataPath);
		blazorOverlayWebView.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping("local.data", dataPath, CoreWebView2HostResourceAccessKind.Allow);
		blazorOverlayWebView.WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
		blazorOverlayWebView.WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
	}

	private static class NativeMethods {
		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
		public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
		public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

		[DllImport("user32.dll", SetLastError = true)]
		public static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

		[DllImport("user32.dll", SetLastError = true)]
		public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
	}
}
