using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TrueFluentPro;

public partial class MainWindow
{
    private bool _songScrollFrameApplied;
    private bool _previousClientAreaExtension;
    private ExtendClientAreaChromeHints _previousChromeHints;
    private double _previousTitleBarHeight;

    private void OnShellTitleBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Border.BackgroundProperty)
            UpdateNativeCaptionColors();
    }

    private void UpdateNativeCaptionColors()
    {
        var scrollActive = IsSongScrollAppearance && SongScrollHost?.IsVisible == true;
        UpdateSongScrollFrame(scrollActive);
        // 旧主题保留 BorderOnly 非客户区的配色修复；长卷则由自绘顶栏接到上沿。
        // 不移除 WS_THICKFRAME，不关闭 DWM 阴影。
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || Design.IsDesignMode
            || TryGetPlatformHandle() is not { Handle: var hwnd } || hwnd == IntPtr.Zero)
            return;

        // 新页顶栏右缘为纸纹上叠 65% 暖白；不从已隐藏的旧标题栏取色。
        if (!scrollActive && ShellTitleBar?.Background is not ISolidColorBrush) return;
        var color = scrollActive ? Color.Parse("#F7F1E8") : ((ISolidColorBrush)ShellTitleBar!.Background!).Color;
        var colorRef = (uint)(color.R | color.G << 8 | color.B << 16);
        SetNativeCaptionColor(hwnd, 35, colorRef); // DWMWA_CAPTION_COLOR：上沿非客户区填色。
        SetNativeCaptionColor(hwnd, 34, scrollActive ? 0xFFFFFFFEu : colorRef); // 长卷不绘制系统外框描边。
        UpdateNativeCornerPreference();
        // Win32 在最大化/还原的 ExtendClientArea 末尾会重设 ROUND，待它完成后再应用。
        Dispatcher.UIThread.Post(UpdateNativeCornerPreference, DispatcherPriority.Loaded);
    }

    private void UpdateSongScrollFrame(bool active)
    {
        if (!OperatingSystem.IsWindows() || Design.IsDesignMode || active == _songScrollFrameApplied) return;
        _songScrollFrameApplied = active;
        if (active)
        {
            _previousClientAreaExtension = ExtendClientAreaToDecorationsHint;
            _previousChromeHints = ExtendClientAreaChromeHints;
            _previousTitleBarHeight = ExtendClientAreaTitleBarHeightHint;
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
            ExtendClientAreaToDecorationsHint = true;
        }
        else
        {
            ExtendClientAreaToDecorationsHint = _previousClientAreaExtension;
            ExtendClientAreaChromeHints = _previousChromeHints;
            ExtendClientAreaTitleBarHeightHint = _previousTitleBarHeight;
        }
    }

    private void UpdateNativeCornerPreference()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || Design.IsDesignMode
            || TryGetPlatformHandle() is not { Handle: var hwnd } || hwnd == IntPtr.Zero) return;
        var active = IsSongScrollAppearance && SongScrollHost?.IsVisible == true;
        // DWMWA_WINDOW_CORNER_PREFERENCE：DONOTROUND=1；旧主题恢复系统默认=0。
        SetNativeCaptionColor(hwnd, 33, active ? 1u : 0u);
    }

    private static void SetNativeCaptionColor(IntPtr hwnd, uint attribute, uint colorRef)
    {
        var result = DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(uint));
        if (result < 0)
            Debug.WriteLine($"窗口装饰未应用：DWM 属性 {attribute}，HRESULT=0x{result:X8}。");
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}