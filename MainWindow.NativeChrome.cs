using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TrueFluentPro;

public partial class MainWindow
{
    private void OnShellTitleBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Border.BackgroundProperty)
            UpdateNativeCaptionColors();
    }

    private void UpdateNativeCaptionColors()
    {
        // BorderOnly 仍有正常态非客户区：11.3.11 不处理它的 NCCALCSIZE，
        // DWM CaptionColor 决定上沿填色（并非标题底部的 1 DIP XAML 分隔线）。
        // 只改颜色，不去掉 WS_THICKFRAME，不改边缘命中、客户区或 DWM 阴影。
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || Design.IsDesignMode
            || ShellTitleBar?.Background is not ISolidColorBrush brush
            || TryGetPlatformHandle() is not { Handle: var hwnd } || hwnd == IntPtr.Zero)
            return;

        var color = brush.Color;
        var colorRef = (uint)(color.R | color.G << 8 | color.B << 16);
        SetNativeCaptionColor(hwnd, 35, colorRef); // DWMWA_CAPTION_COLOR：上沿非客户区填色。
        SetNativeCaptionColor(hwnd, 34, colorRef); // DWMWA_BORDER_COLOR：保留外框，以相同底色融入。
    }

    private static void SetNativeCaptionColor(IntPtr hwnd, uint attribute, uint colorRef)
    {
        var result = DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(uint));
        if (result < 0)
            Debug.WriteLine($"窗口边框颜色未应用：DWM 属性 {attribute}，HRESULT=0x{result:X8}。");
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
}