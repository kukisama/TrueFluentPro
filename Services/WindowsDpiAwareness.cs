using System;
using System.Runtime.InteropServices;

namespace TrueFluentPro.Services;

internal static class WindowsDpiAwareness
{
    private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    private static readonly IntPtr DpiAwarenessContextPerMonitorAware = new(-3);
    private const int ProcessPerMonitorDpiAware = 2;

    public static void TryEnablePerMonitorV2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2))
            {
                return;
            }

            if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAware))
            {
                return;
            }
        }
        catch
        {
            // ignore and try fallback APIs below
        }

        try
        {
            _ = SetProcessDpiAwareness(ProcessPerMonitorDpiAware);
            return;
        }
        catch
        {
            // ignore and try legacy fallback below
        }

        try
        {
            _ = SetProcessDPIAware();
        }
        catch
        {
            // best effort only
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDPIAware();
}
