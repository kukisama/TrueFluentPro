using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TrueFluentPro;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

internal static partial class WindowChromeRegression
{
    internal static async Task VerifyVisualAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("需要真实 Windows 桌面。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var baseline = Environment.GetEnvironmentVariable("CHROME_VISUAL_BASELINE") == "1";
        var root = Path.Combine(RepositoryRoot(), "artifacts", "window-chrome-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + (baseline ? "-baseline" : "-fixed"));
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string s) { report.Add(s); Console.WriteLine(s); }
        var app = Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        MainWindow? window = null;
        var passed = false;
        try
        {
            Log($"ARTIFACTS={root}; BASELINE={baseline}; OS={Environment.OSVersion}");
            Log("INPUT=production MainWindow, isolated nonexistent appearance.json, DataContext=null; no real config/DI/cloud/IME install");
            Log($"ASSEMBLY={typeof(MainWindow).Assembly.Location}");
            app.RequestedThemeVariant = ThemeVariant.Light;
            window = new MainWindow(new ShellAppearancePreferencesService(Path.Combine(root, "appearance.json")))
                { WindowStartupLocation = WindowStartupLocation.Manual };
            var home = window.Screens.All.Where(s => CanFit(window, s)).OrderByDescending(s => s.WorkingArea.Width).First();
            foreach (var screen in window.Screens.All) Log($"SCREEN={screen.Bounds}; work={screen.WorkingArea}; scale={screen.Scaling}");
            Log("DPI=only actual Screens.All/RenderScaling; no offscreen 2x or synthetic DPI evidence");
            foreach (var scale in new[] { 2d, 4d })
                if (!window.Screens.All.Any(s => SameScale(s.Scaling, scale))) Log($"SKIP real {scale * 100}% monitor: absent");
            SetNormalGeometry(window, home);
            window.Show();
            await PlaceAsync(window, home, token);
            await ForegroundAsync(window, token);
            var hwnd = window.TryGetPlatformHandle()!.Handle;
            Check(window.DataContext == null, "隔离失败。");
            CheckStyle(window, hwnd, Log);
            foreach (var appearance in new[] { ShellAppearancePreference.Classic, ShellAppearancePreference.SongStudy, ShellAppearancePreference.InkStudy })
            {
                window.ApplyShellAppearance(appearance);
                await FlushAsync(token);
                await ForegroundAsync(window, token);
                await MoveOffButtonsAsync(window, token);
                var prefix = appearance.ToString().ToLowerInvariant();
                CaptureChrome(window, root, prefix + "-normal", Log, !baseline);
                var bar = Find<Border>(window, "ShellTitleBar");
                var idle = BrushColor(bar.Background);
                Log($"PALETTE={appearance}; title={idle}; foreground={BrushColor(Find<Button>(window, "ShellMinimizeButton").Foreground)}");
                if (appearance != ShellAppearancePreference.InkStudy) continue;

                if (baseline)
                {
                    // 单变量原生对照：不改 WS_THICKFRAME、客户区、阴影或 XAML 分隔线。
                    var style = GetWindowLong(hwnd, -16);
                    var frame = Frame(hwnd);
                    var client = window.PointToScreen(default);
                    var color = ColorRef(idle);
                    var hr = DwmSetWindowAttribute(hwnd, 34, ref color, 4);
                    Log($"AB border-only HRESULT=0x{hr:X8}; color=0x{color:X8}");
                    await FlushAsync(token);
                    CaptureChrome(window, root, prefix + "-border-color-only", Log, false);
                    Check(style == GetWindowLong(hwnd, -16) && frame.Width == Frame(hwnd).Width
                        && frame.Height == Frame(hwnd).Height && client == window.PointToScreen(default), "颜色对照意外改变几何/样式。");
                    color = 0xFFFFFFFF;
                    Log($"AB reset border HRESULT=0x{DwmSetWindowAttribute(hwnd, 34, ref color, 4):X8}");
                    await FlushAsync(token);
                    CaptureChrome(window, root, prefix + "-default-restored", Log, false);
                    color = ColorRef(idle);
                    Log($"AB caption-only HRESULT=0x{DwmSetWindowAttribute(hwnd, 35, ref color, 4):X8}");
                    await FlushAsync(token);
                    CaptureChrome(window, root, prefix + "-caption-color-only", Log, false);
                    color = 0xFFFFFFFF;
                    Log($"AB reset caption HRESULT=0x{DwmSetWindowAttribute(hwnd, 35, ref color, 4):X8}");
                    var originalBackground = window.Background;
                    window.Background = new SolidColorBrush(idle);
                    await FlushAsync(token);
                    CaptureChrome(window, root, prefix + "-window-background-only", Log, false);
                    window.Background = originalBackground;
                    // 固定版本 CustomCaptionProc 保留 -1 自动边框高度才有顶部 resize hit zone。
                    window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
                    window.ExtendClientAreaToDecorationsHint = true;
                    await PlaceAsync(window, home, token);
                    await ForegroundAsync(window, token);
                    await MoveOffButtonsAsync(window, token);
                    CaptureChrome(window, root, prefix + "-extended-client", Log, false);
                    CheckStyle(window, hwnd, Log);
                    await ResizeNativeEdgeAsync(window, hwnd, home, token, Log);
                    Log("AB extended client preserves native WS_THICKFRAME and actual bottom-right resize");
                    continue;
                }

                Check(idle == Color.Parse("#EEE9DF"), "InkStudy 标题栏没有暖纸色。");
                foreach (var name in new[] { "ShellMinimizeButton", "ShellMaximizeButton", "ShellCloseButton" })
                {
                    var button = Find<Button>(window, name);
                    var sample = button.PointToScreen(new Point(8, 8));
                    Check(BrushColor(button.Background).A == 0, "正常态标题按钮不是透明背景。");
                    CaptureChrome(window, root, name + "-idle", Log, true, sample, idle);
                    CheckInputTarget(window, button, Center(button));
                    Check(SetCursorPos(sample.X, sample.Y), "鼠标悬停定位失败。");
                    await UntilAsync(() => button.IsPointerOver, "按钮没有真实 pointerover。", token);
                    await FlushAsync(token);
                    var expectedHover = Color.Parse(name == "ShellCloseButton" ? "#AD4436" : "#E2D8C5");
                    CaptureChrome(window, root, name + "-hover", Log, true, sample, expectedHover);
                    SendChromeInput(new ChromeInput { Data = new ChromeInputUnion { Mouse = new ChromeMouse { Flags = 2 } } });
                    try
                    {
                        await UntilAsync(() => button.IsPressed, "按钮没有真实 pressed。", token);
                        await FlushAsync(token);
                        var expectedPressed = Color.Parse(name == "ShellCloseButton" ? "#8B3028" : "#D2C4AB");
                        CaptureChrome(window, root, name + "-pressed", Log, true, sample, expectedPressed);
                    }
                    finally
                    {
                        // 在按钮外释放，避免采集 close-pressed 时提前关闭窗口。
                        var outside = Find<Border>(window, "ShellTitleDragArea").PointToScreen(new Point(100, 20));
                        SetCursorPos(outside.X, outside.Y);
                        SendChromeInput(new ChromeInput { Data = new ChromeInputUnion { Mouse = new ChromeMouse { Flags = 4 } } });
                    }
                    await MoveOffButtonsAsync(window, token);
                    // KeyboardDevice.SetFocusedElement 仅在目标变化时派发 GotFocus；
                    // 鼠标按下后先 ClearFocus，再用 Tab 方法进入键盘焦点状态。
                    window.FocusManager!.ClearFocus();
                    Check(button.Focus(NavigationMethod.Tab), "按钮拒绝键盘焦点。");
                    await FlushAsync(token);
                    var focus = button.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_CaptionFocus");
                    Log($"FOCUS {name}: focused={button.IsFocused}; opacity={focus.Opacity}; method=ClearFocus+Focus(Tab), real rendered outline (not physical Tab input)");
                    Check(button.IsFocused && focus.Opacity == 1, "实际焦点轮廓没有显示。");
                    var focusSample = focus.PointToScreen(new Point(focus.Bounds.Width / 2, 0));
                    CaptureChrome(window, root, name + "-focus", Log, true, focusSample, Color.Parse("#79613A"));
                    window.FocusManager.ClearFocus();
                    await FlushAsync(token);
                }
                await ClickAsync(window, Find<Button>(window, "ShellMaximizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Maximized, token);
                await MoveOffButtonsAsync(window, token);
                CheckCaption(window, true);
                CheckClientInWorkArea(window, home, Log);
                CaptureChrome(window, root, "ink-study-maximized", Log, true);
                await ClickAsync(window, Find<Button>(window, "ShellMaximizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                await MoveOffButtonsAsync(window, token);
                CheckCaption(window, false);
                CheckStyle(window, hwnd, Log);
                CaptureChrome(window, root, "ink-study-restored", Log, true);

                await ClickAsync(window, Find<Button>(window, "ShellMinimizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Minimized, token);
                ShowWindow(hwnd, 9);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                await ForegroundAsync(window, token);
                var before = Frame(hwnd);
                var drag = Center(Find<Border>(window, "ShellTitleDragArea"));
                CheckInputTarget(window, Find<Border>(window, "ShellTitleDragArea"), drag);
                await FloatingSubtitleRegression.DragAsync(drag, new PixelPoint(drag.X + 24, drag.Y + 16));
                Check(Frame(hwnd).Left > before.Left + 8 && Frame(hwnd).Top > before.Top + 5, "实际拖动失败。");
                await DoubleClickTitleAsync(window, token);
                await StateAsync(window, hwnd, WindowState.Maximized, token);
                await DoubleClickTitleAsync(window, token);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                await PlaceAsync(window, home, token);
                await ResizeNativeEdgeAsync(window, hwnd, home, token, Log);
                Log("PASS native minimize/restore, maximize/restore, drag, double-click, WS_THICKFRAME + HTBOTTOMRIGHT resize");

                // 返回旧主题检查临时 InkStudy 状态不会残留。
                foreach (var old in new[] { ShellAppearancePreference.SongStudy, ShellAppearancePreference.Classic })
                {
                    window.ApplyShellAppearance(old);
                    await FlushAsync(token);
                    await MoveOffButtonsAsync(window, token);
                    Check(!window.Classes.Contains("ink-study"), "返回旧主题仍保留 ink-study。");
                    Check(!Find<Button>(window, "ShellMinimizeButton").GetVisualDescendants().OfType<Border>().Any(b => b.Name == "PART_CaptionFocus"), "旧主题按钮仍残留 InkStudy 模板。");
                    Check(BrushColor(Find<Border>(window, "ShellTitleBar").Background) == Color.Parse(old == ShellAppearancePreference.Classic ? "#F6F7F9" : "#F2EBDD"), "旧主题标题颜色被污染。");
                    CaptureChrome(window, root, "return-" + old, Log, true);
                }
            }
            var closed = 0;
            window.Closed += (_, _) => closed++;
            await ClickAsync(window, Find<Button>(window, "ShellCloseButton"), token);
            await UntilAsync(() => closed == 1 && !IsWindow(hwnd), "物理关闭失败。", token);
            Log("PASS physical close; Closed=1 and original HWND destroyed");
            passed = true;
        }
        catch (Exception ex) { Log("FAIL " + ex); throw; }
        finally
        {
            window?.Close();
            app.RequestedThemeVariant = originalTheme;
            Log($"WINDOW_CHROME_VISUAL_EXIT={(passed ? 0 : 1)}");
            File.WriteAllLines(Path.Combine(root, "result.txt"), report);
        }
    }

    private static async Task MoveOffButtonsAsync(MainWindow window, CancellationToken token)
    {
        var p = Find<Border>(window, "ShellTitleDragArea").PointToScreen(new Point(100, 20));
        Check(SetCursorPos(p.X, p.Y), "无法移动鼠标。");
        await FlushAsync(token);
    }

    private static Color BrushColor(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
    private static uint ColorRef(Color c) => (uint)(c.R | c.G << 8 | c.B << 16);

    private static void CaptureChrome(MainWindow window, string root, string name, Action<string> log, bool verify,
        PixelPoint? sample = null, Color? expectedSample = null)
    {
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        Check(GetForegroundWindow() == hwnd, "不是前台测试窗口，不能把其他窗口截图当作本窗。");
        Check(DwmGetWindowAttribute(hwnd, 9, out NativeRect visible, Marshal.SizeOf<NativeRect>()) == 0, "DWM 可见边界查询失败。");
        var client = window.PointToScreen(default);
        var width = (int)Math.Round(window.ClientSize.Width * window.RenderScaling);
        // 普通态截取真实可见边界（排除不可见 resize padding）；最大化只截工作区内客户区。
        var left = window.WindowState == WindowState.Maximized ? client.X : visible.Left;
        var top = window.WindowState == WindowState.Maximized ? client.Y : visible.Top;
        var imageWidth = window.WindowState == WindowState.Maximized ? width : visible.Width;
        var height = (int)Math.Ceiling(46 * window.RenderScaling) + Math.Max(0, client.Y - top);
        Check(WindowFromPoint(new NativePoint { X = client.X + width / 2, Y = client.Y + 10 }) == hwnd, "截图目标被遮挡。");
        using var bitmap = ScreenPixels(left, top, imageWidth, height);
        bitmap.Save(Path.Combine(root, name + ".png"));
        using var locked = bitmap.Lock();
        Color Pixel(int x, int y)
        {
            var value = Marshal.ReadInt32(locked.Address, y * locked.RowBytes + x * 4);
            return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
        var paper = BrushColor(Find<Border>(window, "ShellTitleBar").Background);
        var rows = new List<string>();
        for (var y = 0; y < Math.Min(height, client.Y - top + 5); y++)
        {
            var white = 0;
            var paperPixels = 0;
            var count = 0;
            for (var x = imageWidth / 3; x < imageWidth * 2 / 3; x++)
            {
                var color = Pixel(x, y);
                if (color.R >= 248 && color.G >= 248 && color.B >= 248) white++;
                if (color == paper) paperPixels++;
                count++;
            }
            rows.Add($"y={top + y} rgb={Pixel(imageWidth / 2, y)} white={white}/{count} paper={paperPixels}/{count}");
            if (verify) Check(white == 0 && paperPixels == count, $"{name} 顶部真实像素未与标题底色连续：{rows[^1]}");
        }
        log($"PIXELS {name}: frameTop={visible.Top}; clientTop={client.Y}; delta={client.Y - visible.Top}; title={paper}; " + string.Join("; ", rows));
        if (sample is { } p && expectedSample is { } expected)
        {
            var actual = Pixel(p.X - left, p.Y - top);
            Check(actual == expected, $"{name} 桌面按钮像素={actual}，应={expected}。");
            log($"PASS BUTTON_PIXEL {name}: actual={actual}; expected={expected}");
        }
        log($"SCREENSHOT={Path.Combine(root, name + ".png")}");
    }

    // BitBlt 读取当前屏幕像素，不用 PrintWindow/RenderTargetBitmap 冒充真实 DWM 绘制。
    private static WriteableBitmap ScreenPixels(int x, int y, int width, int height)
    {
        Check(DwmFlush() == 0, "DWM flush 失败。");
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new ChromeBitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var native = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
        var old = SelectObject(dc, native);
        try
        {
            Check(screen != IntPtr.Zero && dc != IntPtr.Zero && native != IntPtr.Zero && pixels != IntPtr.Zero, "GDI 截图资源创建失败。");
            Check(BitBlt(dc, 0, 0, width, height, screen, x, y, 0x00CC0020), "桌面 BitBlt 失败。");
            var bytes = new byte[width * height * 4];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            for (var i = 3; i < bytes.Length; i += 4) bytes[i] = 255;
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using var buffer = bitmap.Lock();
            for (var row = 0; row < height; row++) Marshal.Copy(bytes, row * width * 4, buffer.Address + row * buffer.RowBytes, width * 4);
            return bitmap;
        }
        finally { SelectObject(dc, old); DeleteObject(native); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }

    private static void SendChromeInput(ChromeInput input)
        => Check(SendInput(1, new[] { input }, Marshal.SizeOf<ChromeInput>()) == 1, "原生鼠标输入失败。");
    [StructLayout(LayoutKind.Sequential)] private struct ChromeInput { public uint Type; public ChromeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct ChromeInputUnion { [FieldOffset(0)] public ChromeMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct ChromeMouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct ChromeBitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint ColorsUsed, ColorsImportant; }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, ChromeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref ChromeBitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out NativeRect value, int size);
}