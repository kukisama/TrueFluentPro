using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Projektanker.Icons.Avalonia;
using TrueFluentPro;

internal static class WindowChromeRegression
{
    private const int WsCaption = 0x00C00000, WsDlgFrame = 0x00400000, WsThickFrame = 0x00040000;

    internal static async Task VerifyAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("窗口标题栏专项需要 Windows 桌面。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        var root = Path.Combine(RepositoryRoot(), "artifacts", "window-chrome-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(root);
        var report = new List<string>();
        void Log(string value) { report.Add(value); Console.WriteLine(value); }
        var app = Application.Current!;
        var originalTheme = app.RequestedThemeVariant;
        MainWindow? window = null;
        var hwnd = IntPtr.Zero;
        var closed = 0;
        var passed = false;
        try
        {
            Log($"ARTIFACTS={root}");
            Log("INPUT=production MainWindow + native SendInput; DataContext=null; no MainVM/config/DI/cloud/installation");
            Log("DPI_POLICY=read-only Screens.All; no system DPI changes or synthetic WM_DPICHANGED; PNGs are rendering-only");
            app.RequestedThemeVariant = ThemeVariant.Light;
            window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual };
            window.Closed += (_, _) => closed++;
            Check(window.DataContext == null, "生产主窗口意外创建了 DataContext，停止隔离验收。");
            var screens = window.Screens.All.ToArray();
            foreach (var screen in screens)
                Log($"SCREEN boundsPx={screen.Bounds}, workPx={screen.WorkingArea}, scaling={screen.Scaling:F3}, primary={screen.IsPrimary}");
            var home = screens.Where(screen => CanFit(window, screen))
                .OrderByDescending(screen => screen.WorkingArea.Width / screen.Scaling).FirstOrDefault();
            Check(home != null, "没有工作区能容纳生产 MinWidth/MinHeight（含边框余量）；不降低生产最小尺寸冒充可见窗口。");
            var targets = new List<Screen> { home! };
            foreach (var scale in new[] { 2d, 4d })
            {
                var matching = screens.Where(screen => SameScale(screen.Scaling, scale)).ToArray();
                var target = matching.FirstOrDefault(screen => CanFit(window, screen));
                if (target == null)
                    Log($"SKIP real {scale * 100:F0}% monitor: " + (matching.Length == 0
                        ? "Screens.All has no matching Scaling; native high-DPI NOT verified."
                        : "work area cannot contain production minimum size; native high-DPI NOT verified."));
                else if (!targets.Any(screen => screen.Bounds == target.Bounds)) targets.Add(target);
            }

            // 先按目标物理工作区设置大小/位置，再 Show；不触发主 VM、真实配置或服务容器。
            SetNormalGeometry(window, home!);
            window.Show();
            hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Check(hwnd != IntPtr.Zero && IsWindow(hwnd), "没有真实桌面 HWND。");
            foreach (var screen in targets)
            {
                token.ThrowIfCancellationRequested();
                await PlaceAsync(window, screen, token);
                await ForegroundAsync(window, token);
                Check(window.DataContext == null, "显示窗口后意外设置了主 VM。");
                CheckStyle(window, hwnd, Log);
                CheckLayout(window, Log);
                CheckCaption(window, false);
                if (screen == home) RenderTitleBars(window, root, "normal", token, Log);

                await ClickAsync(window, Find<Button>(window, "ShellMaximizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Maximized, token);
                CheckCaption(window, true);
                CheckLayout(window, Log);
                CheckClientInWorkArea(window, screen, Log);
                if (screen == home) RenderTitleBars(window, root, "maximized", token, Log);
                await ClickAsync(window, Find<Button>(window, "ShellMaximizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                CheckCaption(window, false);
                CheckStyle(window, hwnd, Log);
                CheckFrameInWorkArea(hwnd, screen);
                Log("PASS physical maximize/restore buttons; IsZoomed, icon/automation names and maximized client work area");

                await ClickAsync(window, Find<Button>(window, "ShellMinimizeButton"), token);
                await StateAsync(window, hwnd, WindowState.Minimized, token);
                // 最小化后按钮不可点：原生 SW_RESTORE 恢复同一个 HWND，不用属性设置冒充最小化点击。
                ShowWindow(hwnd, 9);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                await ForegroundAsync(window, token);
                CheckCaption(window, false);
                CheckFrameInWorkArea(hwnd, screen);
                Log("PASS physical minimize button + IsIconic; native SW_RESTORE to same HWND");

                var beforeMove = Frame(hwnd);
                var drag = Find<Border>(window, "ShellTitleDragArea");
                var start = Center(drag);
                CheckInputTarget(window, drag, start);
                await FloatingSubtitleRegression.DragAsync(start, new PixelPoint(start.X + 24, start.Y + 16));
                token.ThrowIfCancellationRequested();
                var afterMove = Frame(hwnd);
                Check(afterMove.Left > beforeMove.Left + 8 && afterMove.Top > beforeMove.Top + 5,
                    "真实标题拖动没有按输入方向移动 HWND。");
                CheckFrameInWorkArea(hwnd, screen);
                Log($"PASS physical title drag: ({beforeMove.Left},{beforeMove.Top}) -> ({afterMove.Left},{afterMove.Top}) px");

                await DoubleClickTitleAsync(window, token);
                await StateAsync(window, hwnd, WindowState.Maximized, token);
                CheckCaption(window, true);
                CheckClientInWorkArea(window, screen, Log);
                await DoubleClickTitleAsync(window, token);
                await StateAsync(window, hwnd, WindowState.Normal, token);
                CheckCaption(window, false);
                Log("PASS physical title double-click maximize/restore + IsZoomed");

                // 回到可见工作区并留足扩张余量；不添加 grip，不调用 BeginResizeDrag。
                await PlaceAsync(window, screen, token);
                await ResizeNativeEdgeAsync(window, hwnd, screen, token, Log);
                CheckLayout(window, Log);
                if (SameScale(screen.Scaling, 2) || SameScale(screen.Scaling, 4))
                    Log($"PASS real {screen.Scaling * 100:F0}% monitor: native interactions; RenderScaling={window.RenderScaling:F3}, screenPx={screen.Bounds}");
            }

            await ClickAsync(window, Find<Button>(window, "ShellCloseButton"), token);
            await UntilAsync(() => closed == 1 && !IsWindow(hwnd), "真实关闭没有触发 Closed 或旧 HWND 仍存在。", token);
            Check(!window.IsVisible && window.DataContext == null, "关闭后窗口残留或隔离 DataContext 被改变。");
            Log("PASS physical close: Closed=1, IsWindow(old HWND)=false, IsVisible=false");
            token.ThrowIfCancellationRequested();
            passed = true;
        }
        catch (Exception ex)
        {
            Log($"FAIL {ex}");
            throw;
        }
        finally
        {
            try
            {
                window?.Close();
                Check(hwnd == IntPtr.Zero || !IsWindow(hwnd), "finally 清理后测试 HWND 仍残留。");
            }
            catch (Exception ex)
            {
                passed = false;
                Log($"FAIL cleanup: {ex}");
                throw;
            }
            finally
            {
                app.RequestedThemeVariant = originalTheme;
                var status = $"WINDOW_CHROME_EXIT={(passed ? 0 : 1)}";
                report.Add(status);
                File.WriteAllLines(Path.Combine(root, "result.txt"), report);
                Console.WriteLine(status);
            }
        }
    }

    // 工作区内保留边框预算及真实拖动/向右下扩张余量，不篡改生产最小尺寸。
    private static double FrameMargin(Screen screen) => 96 + 32 * screen.Scaling;
    private static bool CanFit(Window window, Screen screen) => screen.Scaling > 0
        && screen.WorkingArea.Width >= window.MinWidth * screen.Scaling + FrameMargin(screen)
        && screen.WorkingArea.Height >= window.MinHeight * screen.Scaling + FrameMargin(screen);

    private static void SetNormalGeometry(Window window, Screen screen)
    {
        window.Width = Math.Max(window.MinWidth, Math.Min(1100, (screen.WorkingArea.Width - FrameMargin(screen)) / screen.Scaling));
        window.Height = Math.Max(window.MinHeight, Math.Min(700, (screen.WorkingArea.Height - FrameMargin(screen)) / screen.Scaling));
        window.Position = new PixelPoint(screen.WorkingArea.X + 24, screen.WorkingArea.Y + 24);
    }

    private static async Task PlaceAsync(Window window, Screen screen, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        window.WindowState = WindowState.Normal;
        window.Position = new PixelPoint(screen.WorkingArea.X + 24, screen.WorkingArea.Y + 24);
        await UntilAsync(() => SameScale(window.RenderScaling, screen.Scaling)
            && window.Screens.ScreenFromWindow(window)?.Bounds == screen.Bounds,
            "移动到目标屏幕后真实 RenderScaling/屏幕归属没有更新。", token);
        SetNormalGeometry(window, screen);
        await FlushAsync(token);
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        var frame = Frame(hwnd);
        window.Position = new PixelPoint(screen.WorkingArea.X + (screen.WorkingArea.Width - frame.Width) / 2,
            screen.WorkingArea.Y + (screen.WorkingArea.Height - frame.Height) / 2);
        await FlushAsync(token);
        CheckFrameInWorkArea(hwnd, screen);
    }

    private static void CheckStyle(Window window, IntPtr hwnd, Action<string> log)
    {
        var style = GetWindowLong(hwnd, -16);
        // Avalonia 11.3.11 WindowImpl.UpdateWindowProperties 的 BorderOnly 保留 WS_BORDER。
        // WS_CAPTION = WS_BORDER | WS_DLGFRAME；不能误用 (style & WS_CAPTION) == 0。
        // https://github.com/AvaloniaUI/Avalonia/blob/11.3.11/src/Windows/Avalonia.Win32/WindowImpl.cs
        Check(window.SystemDecorations == SystemDecorations.BorderOnly && (style & WsCaption) != WsCaption
            && (style & WsDlgFrame) == 0, "系统标题栏未移除（应为 BorderOnly，无 caption/dialog frame）。");
        Check(window.WindowState == WindowState.Normal && (style & WsThickFrame) != 0,
            "Normal 态丢失系统 WS_THICKFRAME 缩放边缘。");
        log($"STYLE GWL_STYLE=0x{style:X8}, caption=false, thickFrame=true, RenderScaling={window.RenderScaling:F3}");
    }

    private static void CheckLayout(Window window, Action<string> log)
    {
        var bar = Find<Border>(window, "ShellTitleBar");
        var drag = Find<Border>(window, "ShellTitleDragArea");
        var title = BoundsIn(bar, window);
        var nav = BoundsIn(Find<Border>(window, "ShellNavRail"), window);
        var page = BoundsIn(Find<Panel>(window, "PageHost"), window);
        Check(title.Width > 0 && title.Height >= 40 && Near(title.Left, 0)
            && Near(title.Right, window.ClientSize.Width) && Near(title.Top, 0), "自绘标题条不是客户区顶部全宽。");
        Check(nav.Width > 0 && page.Width > 0 && nav.Height > 0 && page.Height > 0
            && nav.Top >= title.Bottom - 0.1 && page.Top >= title.Bottom - 0.1,
            "ShellNavRail/PageHost 与标题条重叠或未参与布局。");
        var dragBounds = BoundsIn(drag, bar);
        var previousRight = dragBounds.Right;
        Check(dragBounds.Width > 100 && Near(dragBounds.Height, 40), "拖动区无有效宽度或高度错误。");
        foreach (var name in new[] { "ShellMinimizeButton", "ShellMaximizeButton", "ShellCloseButton" })
        {
            var button = Find<Button>(window, name);
            var bounds = BoundsIn(button, bar);
            // 基线屏幕可能是 125%：生产声明仍须精确 46×40，实际布局允许一物理像素舍入。
            var rounding = 1 / window.RenderScaling + 0.01;
            Check(Near(button.Width, 46) && Near(button.Height, 40)
                && Math.Abs(bounds.Width - 46) <= rounding && Math.Abs(bounds.Height - 40) <= rounding
                && Near(bounds.Left, previousRight)
                && Contains(new Rect(bar.Bounds.Size), bounds), $"{name} 不是 46×40 DIP、与拖动区/相邻按钮重叠或越界。");
            previousRight = bounds.Right;
            var icon = button.GetVisualDescendants().OfType<Icon>().Single();
            Check(icon.IsEffectivelyVisible && icon.Bounds.Width > 0 && icon.Bounds.Height > 0
                && Contains(bounds, BoundsIn(icon, bar)), $"{name} 图标为空或被按钮裁剪。");
            var images = icon.GetVisualDescendants().OfType<Image>().ToArray();
            Check(images.Length > 0, $"{name} 没有实际图标绘制节点。");
            foreach (var image in images)
            {
                Check(image.Source is { } source && source.Size.Width > 0 && source.Size.Height > 0
                    && image.Bounds.Width > 0 && image.Bounds.Height > 0, $"{name} 图标源/绘制尺寸为空。");
                CheckVisibleBounds(image, bar);
            }
            log($"DIP {name}={bounds}, icon={icon.Value}, automation={AutomationProperties.GetName(button)}");
        }
        Check(Near(previousRight, bar.Bounds.Width), "三按钮没有占满标题条右侧或拖动区侵入了按钮区域。");
        var titleText = drag.GetVisualDescendants().OfType<TextBlock>().Single();
        Check(titleText.Text == window.Title, "标题文字未绑定生产窗口 Title。");
        using var fullText = new TextLayout(titleText.Text,
            new Typeface(titleText.FontFamily, titleText.FontStyle, titleText.FontWeight, titleText.FontStretch),
            titleText.FontSize, titleText.Foreground);
        Check(fullText.WidthIncludingTrailingWhitespace <= titleText.Bounds.Width + 0.1
            && fullText.Height <= titleText.Bounds.Height + 0.1, "标题文字实际尺寸被裁剪/省略。");
        CheckVisibleBounds(titleText, bar);
        log($"DIP title={title}, drag={dragBounds}, nav={nav}, page={page}, RenderScaling={window.RenderScaling:F3}");
    }

    private static void CheckVisibleBounds(Visual visual, Visual bar)
    {
        var bounds = BoundsIn(visual, bar);
        Check(Contains(new Rect(bar.Bounds.Size), bounds), "图标/标题文字超出标题条。");
        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor == bar) break;
            Check(Contains(BoundsIn(ancestor, bar), bounds), "图标/标题文字超出其布局容器，可能被裁剪。");
            if (ancestor.Clip != null)
                Check(Contains(ancestor.Clip.Bounds, BoundsIn(visual, ancestor)), "祖先 Clip 裁掉图标/文字边界。");
        }
    }

    private static void CheckCaption(Window window, bool maximized)
    {
        var button = Find<Button>(window, "ShellMaximizeButton");
        var label = maximized ? "还原" : "最大化";
        var icon = Find<Icon>(window, "ShellMaximizeIcon");
        Check(AutomationProperties.GetName(button) == label && Equals(ToolTip.GetTip(button), label)
            && icon.Value == (maximized ? "fa-regular fa-window-restore" : "fa-regular fa-window-maximize"),
            "最大化状态的图标、自动化名称或工具提示没有同步。");
        Check(AutomationProperties.GetName(Find<Button>(window, "ShellMinimizeButton")) == "最小化"
            && AutomationProperties.GetName(Find<Button>(window, "ShellCloseButton")) == "关闭", "三按钮自动化名称缺失。");
    }

    private static void CheckClientInWorkArea(Window window, Screen screen, Action<string> log)
    {
        var first = window.PointToScreen(default);
        var last = window.PointToScreen(new Point(window.ClientSize.Width, window.ClientSize.Height));
        var area = screen.WorkingArea;
        Check(first.X >= area.X - 1 && first.Y >= area.Y - 1 && last.X <= area.Right + 1 && last.Y <= area.Bottom + 1,
            $"最大化客户区越过工作区：{first}..{last} / {area}。");
        var bar = Find<Border>(window, "ShellTitleBar");
        var titleTop = bar.PointToScreen(default);
        var titleBottom = bar.PointToScreen(new Point(bar.Bounds.Width, bar.Bounds.Height));
        Check(bar.IsEffectivelyVisible && bar.Bounds.Height >= 40 && titleTop.Y >= area.Y - 1
            && titleBottom.Y <= area.Bottom && titleBottom.Y > titleTop.Y, "最大化后自绘标题条被移出可见工作区。");
        log($"MAXIMIZED clientPx={first}..{last}, workPx={area}, titlePx={titleTop}..{titleBottom}");
    }

    private static void RenderTitleBars(Window window, string root, string state, CancellationToken token, Action<string> log)
    {
        var bar = Find<Border>(window, "ShellTitleBar");
        Span<byte> header = stackalloc byte[24];
        foreach (var scale in new[] { 2, 4 })
        {
            token.ThrowIfCancellationRequested();
            CheckLayout(window, log);
            var pixels = new PixelSize((int)Math.Ceiling(bar.Bounds.Width * scale), (int)Math.Ceiling(bar.Bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(bar); // 只渲染已布局的生产小条，不截窗口/桌面，不改变原生窗口 DPI。
            Check(bitmap.PixelSize == pixels && Near(bitmap.Dpi.X, 96 * scale) && Near(bitmap.Dpi.Y, 96 * scale)
                && Math.Abs(bitmap.Size.Width - bar.Bounds.Width) <= 1d / scale
                && Math.Abs(bitmap.Size.Height - bar.Bounds.Height) <= 1d / scale, "目标位图像素/DIP/DPI 比例错误。");
            var path = Path.Combine(root, $"titlebar-{state}-{scale}x.png");
            bitmap.Save(path);
            using var file = File.OpenRead(path);
            file.ReadExactly(header);
            Check(header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                && BinaryPrimitives.ReadInt32BigEndian(header[16..20]) == pixels.Width
                && BinaryPrimitives.ReadInt32BigEndian(header[20..24]) == pixels.Height, "落盘 PNG 尺寸与倍率不符。");
            log($"PASS render-only {scale}x: {path}, dip={bar.Bounds.Size}, pixels={pixels}, dpi={bitmap.Dpi}; NOT native DPI validation");
        }
    }

    private static async Task ClickAsync(Window window, Control control, CancellationToken token)
    {
        await ForegroundAsync(window, token);
        CheckInputTarget(window, control, Center(control));
        await FloatingSubtitleRegression.ClickAsync(control);
        token.ThrowIfCancellationRequested();
    }

    private static async Task DoubleClickTitleAsync(Window window, CancellationToken token)
    {
        await ForegroundAsync(window, token);
        // 与上一组点击隔开，防止把三连击误当新双击；下两次复用输入的间隔约为一次 Flush(100ms)。
        await Task.Delay(checked((int)GetDoubleClickTime()) + 30, token);
        Check(GetDoubleClickTime() > 150, "系统双击间隔过短，现有鼠标输入 helper 的 100ms Flush 无法可靠送入双击。");
        var drag = Find<Border>(window, "ShellTitleDragArea");
        var point = Center(drag);
        CheckInputTarget(window, drag, point);
        await FloatingSubtitleRegression.MouseClickAsync(point);
        await FloatingSubtitleRegression.MouseClickAsync(point);
        token.ThrowIfCancellationRequested();
    }

    private static async Task ResizeNativeEdgeAsync(Window window, IntPtr hwnd, Screen screen,
        CancellationToken token, Action<string> log)
    {
        await ForegroundAsync(window, token);
        var before = Frame(hwnd);
        PixelPoint? edge = null;
        // WM_NCHITTEST=HTBOTTOMRIGHT(17)，只选原生边缘；坐标按有符号 16 位打包，兼容负坐标副屏。
        // https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest
        for (var inset = 1; inset <= Math.Ceiling(12 * window.RenderScaling); inset++)
        {
            token.ThrowIfCancellationRequested();
            var point = new PixelPoint(before.Right - inset, before.Bottom - inset);
            Check(point.X >= short.MinValue && point.X <= short.MaxValue && point.Y >= short.MinValue && point.Y <= short.MaxValue,
                "屏幕坐标超出 WM_NCHITTEST 有符号 16 位范围。");
            var packed = new IntPtr(unchecked((int)(((uint)(ushort)point.Y << 16) | (ushort)point.X)));
            Check(SendMessageTimeout(hwnd, 0x0084, IntPtr.Zero, packed, 2, 250, out var hit) != IntPtr.Zero,
                "原生边缘命中查询超时。");
            if (hit.ToInt64() == 17 && WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }) == hwnd)
            { edge = point; break; }
        }
        Check(edge.HasValue, "真实右下系统边缘没有返回 HTBOTTOMRIGHT；不能改用测试 grip 冒充。");
        var nonClientDown = false;
        var sizingMessages = 0;
        Win32Properties.CustomWndProcHookCallback hook = (IntPtr handle, uint message, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (message == 0x00A1 && wp.ToInt64() == 17) nonClientDown = true;
            if (message == 0x0214) sizingMessages++;
            return IntPtr.Zero; // 只观测，不拦截、不伪造非客户区消息。
        };
        Win32Properties.AddWndProcHookCallback(window, hook);
        try
        {
            var start = edge!.Value;
            await FloatingSubtitleRegression.DragAsync(start, new PixelPoint(start.X + 24, start.Y + 16));
            token.ThrowIfCancellationRequested();
            var after = Frame(hwnd);
            Check(nonClientDown && sizingMessages > 0 && after.Width > before.Width + 8 && after.Height > before.Height + 5,
                "未同时观测到系统 NC 边缘按下、WM_SIZING 和真实宽高增长。");
            CheckFrameInWorkArea(hwnd, screen);
            log($"PASS native bottom-right resize: hit=17, NC_DOWN={nonClientDown}, WM_SIZING={sizingMessages}, px={before.Width}x{before.Height}->{after.Width}x{after.Height}");
        }
        finally { Win32Properties.RemoveWndProcHookCallback(window, hook); }
    }

    private static async Task ForegroundAsync(Window window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        window.Activate();
        await FlushAsync(token);
        if (GetForegroundWindow() != window.TryGetPlatformHandle()?.Handle)
        {
            // SetForegroundWindow 可因前台锁被拒绝。只临时置顶测试窗并点击已核验的自身标题区，
            // 不解除系统前台锁，不向其他窗口注入输入，也不把 Activate 返回当作取得前台。
            var wasTopmost = window.Topmost;
            try
            {
                window.Topmost = true;
                await FlushAsync(token);
                var point = Center(Find<Border>(window, "ShellTitleDragArea"));
                Check(WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }) == window.TryGetPlatformHandle()?.Handle,
                    "无法安全点击测试窗取得焦点：目标不属于当前测试 HWND。");
                await FloatingSubtitleRegression.MouseClickAsync(point);
            }
            finally { window.Topmost = wasTopmost; }
        }
        await UntilAsync(() => GetForegroundWindow() == window.TryGetPlatformHandle()?.Handle,
            "测试 HWND 未获得前台；不向其他应用注入输入。", token);
    }

    private static void CheckInputTarget(Window window, Control control, PixelPoint point)
    {
        var hit = window.InputHitTest(window.PointToClient(point)) as Visual;
        Check(GetForegroundWindow() == window.TryGetPlatformHandle()?.Handle
            && WindowFromPoint(new NativePoint { X = point.X, Y = point.Y }) == window.TryGetPlatformHandle()?.Handle
            && control.IsEffectivelyVisible && control.IsEffectivelyEnabled
            && (hit == control || hit?.GetVisualAncestors().Contains(control) == true),
            "真实鼠标目标被遮挡、不可用或命中其他窗口，停止输入。");
    }

    private static async Task StateAsync(Window window, IntPtr hwnd, WindowState state, CancellationToken token)
    {
        await UntilAsync(() => window.WindowState == state && IsIconic(hwnd) == (state == WindowState.Minimized)
            && IsZoomed(hwnd) == (state == WindowState.Maximized), $"Avalonia 与原生窗口状态未同时进入 {state}。", token);
        await FlushAsync(token);
    }

    private static async Task UntilAsync(Func<bool> ready, string error, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), error);
            await Task.Delay(20, token);
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task FlushAsync(CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100, token);
    }

    private static T Find<T>(Window window, string name) where T : Control => window.FindControl<T>(name)
        ?? throw new InvalidOperationException($"生产窗口缺少 {name}。");
    private static PixelPoint Center(Control control) => control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
    private static Rect BoundsIn(Visual visual, Visual ancestor) => new(visual.TranslatePoint(default, ancestor)
        ?? throw new InvalidOperationException("控件不在同一生产视觉树。"), visual.Bounds.Size);
    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 0.1 && inner.Top >= outer.Top - 0.1
        && inner.Right <= outer.Right + 0.1 && inner.Bottom <= outer.Bottom + 0.1;
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.1;
    private static bool SameScale(double a, double b) => Math.Abs(a - b) < 0.001;
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static NativeRect Frame(IntPtr hwnd)
    {
        Check(GetWindowRect(hwnd, out var rect), "无法读取测试 HWND 窗口坐标。");
        return rect;
    }
    private static void CheckFrameInWorkArea(IntPtr hwnd, Screen screen)
    {
        var rect = Frame(hwnd);
        var area = screen.WorkingArea;
        Check(rect.Left >= area.X && rect.Top >= area.Y && rect.Right <= area.Right && rect.Bottom <= area.Bottom,
            $"Normal 窗口坐标越过工作区：{rect.Left},{rect.Top}..{rect.Right},{rect.Bottom} / {area}。");
    }
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "TrueFluentPro.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("找不到仓库根 TrueFluentPro.csproj，不能确定测试产物目录。");
    }

    // 仅本专项需要的 HWND 查询；鼠标 SendInput/结构体/拖动实现全部复用字幕测试。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp,
        uint flags, uint timeout, out IntPtr result);
}