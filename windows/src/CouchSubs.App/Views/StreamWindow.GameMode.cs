// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CouchSubs.App.Views;

/// <summary>
/// Fork: oyun modu. PS5 görüntüsü <see cref="GameModeWindow"/>'da (WPF'siz, DirectX, tam ekran) en düşük gecikmeyle
/// gösterilir; altyazı, kutular ve ek alanlar bu pencerenin ikinci bir kopyasında (katman: saydam, tıklamaları
/// geçirir, en üstte) aynı yerlerine çizilir. Asıl yayın penceresi arkada açık kalır: Discord'daki yayın kesilmez.
/// Esc ya da F11 (oyun penceresinde) ve menüdeki seçenek oyun modunu kapatır.
/// </summary>
public sealed partial class StreamWindow
{
    /// <summary>Bu pencere oyun modunun altyazı katmanı mı (görüntü çizmez, saydam, tıklamaları geçirir).</summary>
    private readonly bool overlay;
    private GameModeWindow? gameMode;
    private StreamWindow? gameOverlay;

    private bool GameModeOn => gameMode is not null;

    /// <summary>Katman penceresinin görünüşü (kurucuda, gösterilmeden önce).</summary>
    private void InitOverlay()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "CouchSubs";
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongW(handle, GwlExStyle);
            SetWindowLongW(handle, GwlExStyle, style | WsExTransparent | WsExLayered | WsExNoActivate | WsExToolWindow);
        };
    }

    /// <summary>Oyun modunu açar ya da kapatır (menü ve G tuşu).</summary>
    private void ToggleGameMode()
    {
        if (GameModeOn)
        {
            StopGameMode();
            return;
        }
        if (!controller.GameVideoActive) return;
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromWindow(handle, 2), ref info)) return;
        var screen = info.rcMonitor;
        gameMode = GameModeWindow.Open(controller.GameVideo, new Vortice.RawRect(screen.Left, screen.Top, screen.Right, screen.Bottom),
            () => controller.StreamSuperResolution,
            line => CouchSubs.RemotePlay.RemotePlayLog.Note($"yayın penceresi: {line}"),
            () => Dispatcher.BeginInvoke(StopGameMode));
        // Katman, oyun penceresiyle aynı ekranı kaplar (WPF birimine çevrilmiş).
        var dpi = VisualTreeHelper.GetDpi(this);
        gameOverlay = new StreamWindow(controller, overlay: true)
        {
            Left = screen.Left / dpi.DpiScaleX,
            Top = screen.Top / dpi.DpiScaleY,
            Width = (screen.Right - screen.Left) / dpi.DpiScaleX,
            Height = (screen.Bottom - screen.Top) / dpi.DpiScaleY,
        };
        gameOverlay.Show();
    }

    private void StopGameMode()
    {
        gameMode?.Dispose();
        gameMode = null;
        gameOverlay?.Close();
        gameOverlay = null;
    }

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20, WsExLayered = 0x80000, WsExNoActivate = 0x08000000, WsExToolWindow = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint handle, int index, int value);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
