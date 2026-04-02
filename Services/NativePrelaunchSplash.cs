using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TrueFluentPro.Services;

internal static class NativePrelaunchSplash
{
    private const int WindowWidth = 360;
    private const int WindowHeight = 140;
    private const int ProgressBarX = 24;
    private const int ProgressBarY = 98;
    private const int ProgressBarWidth = 312;
    private const int ProgressBarHeight = 6;
    private const int ProgressSegmentWidth = 72;
    private const int IconX = 18;
    private const int IconY = 16;
    private const int IconSize = 64;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmTimer = 0x0113;
    private const uint WmEraseBkgnd = 0x0014;
    private const int SwShownormal = 1;
    private const int TransparentBkMode = 1;
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;
    private const uint ExToolWindow = 0x00000080;
    private const uint ExTopmost = 0x00000008;
    private const uint ExNoActivate = 0x08000000;
    private const uint TimerId = 1;
    private const uint TimerInterval = 24;
    private const int DtLeft = 0x00000000;
    private const int DtSingleline = 0x00000020;
    private const int DtVcenter = 0x00000004;
    private const int DtEndEllipsis = 0x00008000;
    private const uint Srccopy = 0x00CC0020;

    private static readonly object SyncRoot = new();
    private static readonly AutoResetEvent WindowReady = new(false);

    private static IntPtr _windowHandle = IntPtr.Zero;
    private static Thread? _uiThread;
    private static string _titleText = "TrueFluentPro";
    private static string _statusText = "正在启动...";
    private static int _progressOffset;
    private static bool _isRegistered;
    private static readonly string ProductIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");

    public static void Show(string title = "TrueFluentPro", string status = "正在启动...")
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_uiThread is { IsAlive: true })
            {
                return;
            }

            _titleText = string.IsNullOrWhiteSpace(title) ? "TrueFluentPro" : title;
            _statusText = string.IsNullOrWhiteSpace(status) ? "正在启动..." : status;
            _progressOffset = 0;
            WindowReady.Reset();

            _uiThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "NativePrelaunchSplash"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
        }

        WindowReady.WaitOne(1500);
    }

    public static void CloseIfOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr hwnd;
        lock (SyncRoot)
        {
            hwnd = _windowHandle;
        }

        if (hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void RunMessageLoop()
    {
        IntPtr instanceHandle = GetModuleHandle(null);
        string className = "TrueFluentPro.NativePrelaunchSplash";

        if (!_isRegistered)
        {
            var windowClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = WindowProc,
                hInstance = instanceHandle,
                hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
                lpszClassName = className
            };

            RegisterClassEx(ref windowClass);
            _isRegistered = true;
        }

        int screenWidth = GetSystemMetrics(0);
        int screenHeight = GetSystemMetrics(1);
        int x = Math.Max(0, (screenWidth - WindowWidth) / 2);
        int y = Math.Max(0, (screenHeight - WindowHeight) / 2);

        IntPtr hwnd = CreateWindowEx(
            ExToolWindow | ExTopmost | ExNoActivate,
            className,
            _titleText,
            WsPopup | WsVisible,
            x,
            y,
            WindowWidth,
            WindowHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            instanceHandle,
            IntPtr.Zero);

        lock (SyncRoot)
        {
            _windowHandle = hwnd;
        }

        ShowWindow(hwnd, SwShownormal);
        UpdateWindow(hwnd);
        SetTimer(hwnd, TimerId, TimerInterval, IntPtr.Zero);
        WindowReady.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        lock (SyncRoot)
        {
            _windowHandle = IntPtr.Zero;
            _uiThread = null;
        }
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmEraseBkgnd:
                return (IntPtr)1;

            case WmTimer:
                _progressOffset = (_progressOffset + 8) % (ProgressBarWidth + 72);
                var progressRect = new Rect(ProgressBarX - 2, ProgressBarY - 2, ProgressBarX + ProgressBarWidth + 2, ProgressBarY + ProgressBarHeight + 2);
                InvalidateRect(hwnd, ref progressRect, false);
                return IntPtr.Zero;

            case WmPaint:
                Paint(hwnd);
                return IntPtr.Zero;

            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                KillTimer(hwnd, TimerId);
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void Paint(IntPtr hwnd)
    {
        BeginPaint(hwnd, out var paintStruct);
        IntPtr hdc = paintStruct.hdc;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr memoryBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        IntPtr backgroundBrush = CreateSolidBrush(0x00FFFFFF);
        IntPtr borderBrush = CreateSolidBrush(0x00D9E2F0);
        IntPtr accentBrush = CreateSolidBrush(0x00EB6325);
        IntPtr accentSoftBrush = CreateSolidBrush(0x00F5F9FF);
        IntPtr badgeBrush = CreateSolidBrush(0x00EB6325);
        IntPtr progressBackgroundBrush = CreateSolidBrush(0x00EEF3F8);

        IntPtr titleFont = CreateFontW(-24, 0, 0, 0, 600, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");
        IntPtr statusFont = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");
        IntPtr gdipToken = IntPtr.Zero;
        IntPtr gdipGraphics = IntPtr.Zero;
        IntPtr gdipImage = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(hdc);
            memoryBitmap = CreateCompatibleBitmap(hdc, WindowWidth, WindowHeight);
            oldBitmap = SelectObject(memoryDc, memoryBitmap);

            var fullRect = new Rect(0, 0, WindowWidth, WindowHeight);
            FillRect(memoryDc, ref fullRect, backgroundBrush);
            FrameRect(memoryDc, ref fullRect, borderBrush);

            bool drewProductImage = TryDrawProductIcon(memoryDc, paintStruct.rcPaint, ref gdipToken, ref gdipGraphics, ref gdipImage);
            if (!drewProductImage)
            {
                var badgeRect = new Rect(24, 22, 68, 66);
                FillRect(memoryDc, ref badgeRect, badgeBrush);

                SetBkMode(memoryDc, TransparentBkMode);
                SetTextColor(memoryDc, 0x00FFFFFF);
                IntPtr fallbackFont = SelectObject(memoryDc, titleFont);
                TextOutW(memoryDc, 40, 32, "译", 1);
                SelectObject(memoryDc, fallbackFont);
            }

            SetBkMode(memoryDc, TransparentBkMode);
            SetTextColor(memoryDc, 0x00262B33);
            IntPtr oldFont = SelectObject(memoryDc, titleFont);
            var titleRect = new Rect(96, 24, WindowWidth - 24, 56);
            DrawTextW(memoryDc, _titleText, _titleText.Length, ref titleRect, DtLeft | DtSingleline | DtVcenter | DtEndEllipsis);
            SelectObject(memoryDc, oldFont);

            SetTextColor(memoryDc, 0x007A7F87);
            oldFont = SelectObject(memoryDc, statusFont);
            var statusRect = new Rect(96, 58, WindowWidth - 24, 82);
            DrawTextW(memoryDc, _statusText, _statusText.Length, ref statusRect, DtLeft | DtSingleline | DtVcenter | DtEndEllipsis);
            SelectObject(memoryDc, oldFont);

            var progressOuterRect = new Rect(ProgressBarX, ProgressBarY, ProgressBarX + ProgressBarWidth, ProgressBarY + ProgressBarHeight);
            FillRect(memoryDc, ref progressOuterRect, progressBackgroundBrush);
            FrameRect(memoryDc, ref progressOuterRect, accentSoftBrush);

            int segmentLeft = ProgressBarX + _progressOffset - ProgressSegmentWidth;
            int segmentRight = segmentLeft + ProgressSegmentWidth;
            int clippedLeft = Math.Max(segmentLeft, ProgressBarX);
            int clippedRight = Math.Min(segmentRight, ProgressBarX + ProgressBarWidth);
            if (clippedRight > clippedLeft)
            {
                var progressInnerRect = new Rect(clippedLeft, ProgressBarY, clippedRight, ProgressBarY + ProgressBarHeight);
                FillRect(memoryDc, ref progressInnerRect, accentBrush);
            }

            SetTextColor(memoryDc, 0x007A7F87);
            oldFont = SelectObject(memoryDc, statusFont);
            TextOutW(memoryDc, 24, 114, "正在准备主界面...", 11);
            SelectObject(memoryDc, oldFont);

            int updateWidth = Math.Max(1, paintStruct.rcPaint.Right - paintStruct.rcPaint.Left);
            int updateHeight = Math.Max(1, paintStruct.rcPaint.Bottom - paintStruct.rcPaint.Top);
            BitBlt(hdc, paintStruct.rcPaint.Left, paintStruct.rcPaint.Top, updateWidth, updateHeight, memoryDc, paintStruct.rcPaint.Left, paintStruct.rcPaint.Top, Srccopy);
        }
        finally
        {
            if (gdipImage != IntPtr.Zero)
            {
                GdipDisposeImage(gdipImage);
            }

            if (gdipGraphics != IntPtr.Zero)
            {
                GdipDeleteGraphics(gdipGraphics);
            }

            if (gdipToken != IntPtr.Zero)
            {
                GdiplusShutdown(gdipToken);
            }

            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (memoryBitmap != IntPtr.Zero)
            {
                DeleteObject(memoryBitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            DeleteObject(titleFont);
            DeleteObject(statusFont);
            DeleteObject(backgroundBrush);
            DeleteObject(borderBrush);
            DeleteObject(accentBrush);
            DeleteObject(accentSoftBrush);
            DeleteObject(badgeBrush);
            DeleteObject(progressBackgroundBrush);
            EndPaint(hwnd, ref paintStruct);
        }
    }

    private static bool TryDrawProductIcon(IntPtr hdc, Rect updateRect, ref IntPtr gdipToken, ref IntPtr gdipGraphics, ref IntPtr gdipImage)
    {
        if (!File.Exists(ProductIconPath))
        {
            return false;
        }

        var iconRect = new Rect(IconX, IconY, IconX + IconSize, IconY + IconSize);
        if (!Intersects(updateRect, iconRect))
        {
            return false;
        }

        var startupInput = new GdiplusStartupInput
        {
            GdiplusVersion = 1,
            DebugEventCallback = IntPtr.Zero,
            SuppressBackgroundThread = false,
            SuppressExternalCodecs = false
        };

        int status = GdiplusStartup(out gdipToken, ref startupInput, IntPtr.Zero);
        if (status != 0)
        {
            gdipToken = IntPtr.Zero;
            return false;
        }

        status = GdipCreateFromHDC(hdc, out gdipGraphics);
        if (status != 0)
        {
            gdipGraphics = IntPtr.Zero;
            return false;
        }

        status = GdipLoadImageFromFile(ProductIconPath, out gdipImage);
        if (status != 0)
        {
            gdipImage = IntPtr.Zero;
            return false;
        }

        status = GdipDrawImageRectI(gdipGraphics, gdipImage, IconX, IconY, IconSize, IconSize);
        return status == 0;
    }

    private static bool Intersects(Rect a, Rect b)
    {
        return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr hdc;
        public int fErase;
        public Rect rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SuppressBackgroundThread;
        [MarshalAs(UnmanagedType.Bool)]
        public bool SuppressExternalCodecs;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx([In] ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref Msg lpmsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, [In] ref PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, [In] ref Rect lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern int FrameRect(IntPtr hDC, [In] ref Rect lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll", EntryPoint = "InvalidateRect")]
    private static extern bool InvalidateRect(IntPtr hWnd, [In] ref Rect lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern uint SetTimer(IntPtr hWnd, uint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, uint uIDEvent);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorRef);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int nHeight,
        int nWidth,
        int nEscapement,
        int nOrientation,
        int fnWeight,
        uint fdwItalic,
        uint fdwUnderline,
        uint fdwStrikeOut,
        uint fdwCharSet,
        uint fdwOutputPrecision,
        uint fdwClipPrecision,
        uint fdwQuality,
        uint fdwPitchAndFamily,
        string lpszFace);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string lpchText, int cchText, ref Rect lprc, int format);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern void GdiplusShutdown(IntPtr token);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipCreateFromHDC(IntPtr hdc, out IntPtr graphics);

    [DllImport("gdiplus.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GdipLoadImageFromFile(string filename, out IntPtr image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", ExactSpelling = true)]
    private static extern int GdipDeleteGraphics(IntPtr graphics);
}