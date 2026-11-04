using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Projektanker.Icons.Avalonia;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Audio;
using TrueFluentPro.ViewModels;
using TrueFluentPro.Views;

internal static class FloatingSubtitleRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task VerifyAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本专项需要 Windows 桌面。");
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "floating-subtitle-validation",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        Console.WriteLine($"ARTIFACTS={root}");
        Console.WriteLine("INPUT=controlled subtitle snapshots; UI=production Avalonia windows and native SendInput; CLOUD=not used");

        // 仅使用生产的恢复命令及菜单，不运行主 VM 构造函数中的配置加载、设备刷新和联网初始化。
        var shell = (MainWindowViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainWindowViewModel));
        var manager = new FloatingSubtitleManager();
        Field(typeof(MainWindowViewModel), "_floatingSubtitleManager").SetValue(shell, manager);
        Field(typeof(MainWindowViewModel), "_isFloatingSubtitleOpen").SetValue(shell, true);
        var liveView = new LiveTranslationView { DataContext = shell };
        var owner = new Window { Title = "字幕真机验收：生产字幕菜单", Width = 1000, Height = 650, Content = liveView };
        var underlyingClicks = 0;
        var underButton = new Button
        {
            Content = "下层点击验证", HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Background = Avalonia.Media.Brushes.SteelBlue
        };
        underButton.Click += (_, _) => underlyingClicks++;
        var underlay = new Window
        {
            Title = "字幕穿透下层验证窗口", SystemDecorations = SystemDecorations.None,
            Width = 620, Height = 200, Content = underButton
        };
        FloatingSubtitleWindow? window = null;
        try
        {
            owner.Show();
            owner.Position = new PixelPoint(40, 40);
            underlay.Show();
            manager.OpenWindow();
            Check(manager.IsWindowOpen, "生产浮窗未打开");
            window = (FloatingSubtitleWindow)Field(typeof(FloatingSubtitleManager), "_window").GetValue(manager)!;
            window.Width = 620;
            window.Height = 200;
            // 原生输入验收固定在可见桌面内部，不让窗口逐步拉高后越过屏幕底边。
            window.Position = new PixelPoint(240, 150);
            var vm = (FloatingSubtitleViewModel)window.DataContext!;
            var text = window.FindControl<TextBlock>("SubtitleTextBlock")!;
            var host = window.FindControl<Border>("SubtitleHost")!;
            var toolbar = window.FindControl<Border>("HoverToolbar")!;
            await FlushAsync();
            Check(window.TryGetPlatformHandle()?.Handle != IntPtr.Zero, "没有真实桌面 HWND");

            var sentence = "我们希望字幕最多显示两行，文字不要一直横向移动。这样阅读时眼睛就能停留在固定位置，长句按页推进，保留足够的阅读时间。"
                + "调整窗口宽度会改变每行的容量，调整高度可以放大字体；还可以锁定字号，并在暂停讲话后继续追加内容。";
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(1, sentence, false, VadGateController.ActiveSource.Mic));
            await FlushAsync();
            Check(text.TextLayout.TextLines.Count == 2, $"实际未形成两行：{text.TextLayout.TextLines.Count}");
            Check(text.TextLayout.TextLines.All(line => !line.HasOverflowed), "实际字形横向溢出");
            Check(text.Bounds.Height <= host.Bounds.Height - host.Padding.Top - host.Padding.Bottom + 1, "两行文字被容器裁高");
            var toolbarBottom = toolbar.TranslatePoint(new Point(0, toolbar.Bounds.Height), window)!.Value.Y;
            var textTop = text.TranslatePoint(default, window)!.Value.Y;
            Check(textTop >= toolbarBottom, "控制栏覆盖字幕正文");
            Check(toolbar.Bounds.Height <= 28 && textTop - toolbarBottom <= 5, "顶部控制区保留空间过高");
            Check(text.RenderTransform == null, "字幕仍有横向位移");
            var firstEnd = Value<int>(window, "_pageEnd");
            Check(firstEnd > 0 && firstEnd < sentence.Length, "长句未被分成多页");
            Console.WriteLine($"PASS rendered two lines, no overflow/overlay/translation; pageEnd={firstEnd}, font={text.FontSize:F2}, host={host.Bounds.Size}");
            Save(window, Path.Combine(root, "two-lines.png"));

            await UntilAsync(() => Value<int>(window, "_pageStart") > 0, "真实分页计时器没有推进", 8);
            var advancedStart = Value<int>(window, "_pageStart");
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(1, sentence + "继续补充。", true, VadGateController.ActiveSource.Mic));
            await FlushAsync();
            Check(Value<int>(window, "_pageStart") == advancedStart, "同句追加/终稿导致重复回首页");
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(1, "最终结论。", true, VadGateController.ActiveSource.Mic));
            await FlushAsync();
            Check(text.Text == "最终结论。" && Value<int>(window, "_pageStart") == 0, "大幅缩短终稿只显示尾字/标点");
            Console.WriteLine("PASS real timer page advance, append/final merge and rewritten final restart");

            var visibleTop = text.TranslatePoint(default, window)!.Value.Y;
            Check(visibleTop - toolbarBottom <= 5, "短句仍在剩余区域垂直居中，顶部出现大块留白");
            var followWatch = Stopwatch.StartNew();
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(2, "那又怎样？", true, VadGateController.ActiveSource.Mic));
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(3, "价值差距正在扩大。", false, VadGateController.ActiveSource.Mic));
            Check(vm.FormattedSubtitleText == "价值差距正在扩大。" && Value<int>(window, "_pageStart") == 0,
                "新句仍在等待旧句阅读计时或排队");
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(1, "迟到的旧终稿。", true, VadGateController.ActiveSource.Mic));
            Check(vm.FormattedSubtitleText == "价值差距正在扩大。", "旧句终稿把浮窗拉回历史内容");
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(4, "第四句实时内容。", false, VadGateController.ActiveSource.Mic));
            manager.UpdateSubtitle(new FloatingSubtitleUpdate(5, "第五句最新内容。", true, VadGateController.ActiveSource.Mic));
            Check(vm.FormattedSubtitleText == "第五句最新内容。", "快速连续新句产生待播积压");
            followWatch.Stop();
            await FlushAsync();
            Console.WriteLine($"PASS immediate new-sentence follow, no backlog/old-final replay; dispatchMs={followWatch.Elapsed.TotalMilliseconds:F1}, toolbarHeight={toolbar.Bounds.Height:F1}, textGap={visibleTop - toolbarBottom:F1}");
            Save(window, Path.Combine(root, "compact-live-follow.png"));

            var fontBeforeWidth = text.FontSize;
            window.Width = 820;
            await FlushAsync();
            Check(Near(text.FontSize, fontBeforeWidth), "只拉宽窗口改变了字号");
            window.Height = 280;
            await FlushAsync();
            Check(text.FontSize > fontBeforeWidth + 5, "拉高窗口没有放大自动字号");
            await ClickAsync(IconButton(window, "fa-solid fa-lock-open"));
            Check(vm.IsFontSizeLocked, "真实点击没有锁定字号");
            var lockedFont = text.FontSize;
            window.Height = 350;
            await FlushAsync();
            Check(Near(text.FontSize, lockedFont), "锁定后调整高度仍改变字号");
            await ClickAsync(IconButton(window, "fa-solid fa-plus"));
            Check(text.FontSize > lockedFont, "真实加号点击未改变固定字号");
            await ClickAsync(IconButton(window, "fa-solid fa-rotate-left"));
            Check(!vm.IsFontSizeLocked && Near(vm.FontScaleBias, 1), "重置没有恢复自动字号");
            Console.WriteLine("PASS native clicks, width-only reflow, automatic height scaling, lock and font reset");

            var captionHandle = window.TryGetPlatformHandle()!.Handle;
            Check(GetForegroundWindow() == captionHandle, "字幕未获得前台，不能把发送按键算作真机键盘通过");
            window.AddHandler(InputElement.KeyDownEvent,
                (_, key) => Console.WriteLine($"NATIVE_KEY key={key.Key}, modifiers={key.KeyModifiers}, handled={key.Handled}, width={window.Width}"),
                Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            var bias = vm.FontScaleBias;
            await KeyAsync(0x11, 0x6B);
            Check(vm.FontScaleBias > bias, "物理 Ctrl+加号未响应");
            var oldPosition = window.Position;
            await KeyAsync(0x12, 0x27);
            Check(window.Position.X == oldPosition.X + 10, "物理 Alt+右方向未移动窗口");
            var oldWidth = window.Width;
            await KeyAsync(new ushort[] { 0x11, 0x12 }, 0x27);
            Check(Near(window.Width, oldWidth + 20), "物理 Ctrl+Alt+右方向未缩放");
            await KeyAsync(0x11, 0x30);
            Check(Near(vm.FontScaleBias, 1), "物理 Ctrl+0 未重置");
            Console.WriteLine("PASS physical Ctrl font controls and Alt movement/resizing with foreground verified");

            var startPosition = window.Position;
            var surface = window.FindControl<Border>("MainBorder")!;
            var dragStart = surface.PointToScreen(new Point(40, 20));
            await DragAsync(dragStart, new PixelPoint(dragStart.X + 50, dragStart.Y - 25));
            Check(window.Position != startPosition, "真实鼠标拖动没有移动字幕窗口");
            var sizeBefore = window.ClientSize;
            var grip = window.FindControl<Border>("ResizeGrip")!;
            var gripPoint = grip.PointToScreen(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2));
            await DragAsync(gripPoint, new PixelPoint(gripPoint.X + 70, gripPoint.Y + 40));
            Check(window.ClientSize.Width > sizeBefore.Width + 10 && window.ClientSize.Height > sizeBefore.Height + 5,
                "真实缩放把手没有调整字幕框");
            Console.WriteLine($"PASS physical drag and resize; position={window.Position}, size={window.ClientSize}");

            for (var i = 0; i < 4; i++) await ClickAsync(IconButton(window, "fa-solid fa-circle-half-stroke"));
            Check(vm.BackgroundMode == 0, "四种背景未循环回默认状态");
            Console.WriteLine("PASS native background controls");

            // 下层按钮与字幕正文区域重叠：验证事件实际穿过 HWND，而非只看样式位。
            underlay.Width = window.Width;
            underlay.Height = window.Height;
            underlay.Position = window.Position;
            // 激活下层窗口，保证它在普通窗口的最前；字幕仍通过 Topmost 位于它上方。
            underlay.Activate();
            await FlushAsync();
            window.Activate();
            await FlushAsync();
            var bodyPoint = host.PointToScreen(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2));
            await MouseClickAsync(bodyPoint);
            Check(underlyingClicks == 0, "关闭穿透时点击仍到了下层");
            await ClickAsync(IconButton(window, "fa-solid fa-hand-pointer"));
            Check(vm.IsClickThrough && (GetWindowLong(captionHandle, -20) & 0x20) != 0, "穿透原生样式未设置");
            Console.WriteLine($"PASS_THROUGH point={bodyPoint}, target={WindowFromPoint(new NativePoint { X = bodyPoint.X, Y = bodyPoint.Y })}, caption={captionHandle}, underlay={underlay.TryGetPlatformHandle()?.Handle}, buttonBounds={underButton.Bounds}, buttonHit={underlay.InputHitTest(underlay.PointToClient(bodyPoint))?.GetType().Name}");
            Check(WindowFromPoint(new NativePoint { X = bodyPoint.X, Y = bodyPoint.Y }) == underlay.TryGetPlatformHandle()?.Handle,
                "验收前提不满足：穿透后的目标不是下层验证窗口");
            await MouseClickAsync(bodyPoint);
            Check(underlyingClicks == 1, $"穿透未把真实点击交给下层按钮：{underlyingClicks}");

            // 使用生产 LiveTranslationView 的真实菜单项及生产命令绑定恢复，不换一个测试命令冒充。
            var split = liveView.GetVisualDescendants().OfType<ToggleSplitButton>().Single();
            var menu = (MenuFlyout)split.Flyout!;
            owner.Activate();
            menu.ShowAt(split);
            await FlushAsync();
            var restore = menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(item => item.Command == shell.RestoreFloatingSubtitleInteractionCommand);
            Check(restore.IsEnabled, "生产恢复菜单不可用或命令未绑定");
            await ClickAsync(restore);
            await FlushAsync();
            Check(!vm.IsClickThrough && (GetWindowLong(captionHandle, -20) & 0x20) == 0, "生产恢复菜单未关闭穿透");
            await MouseClickAsync(bodyPoint);
            Check(underlyingClicks == 1, "恢复后仍有点击穿过字幕");
            Console.WriteLine("PASS physical click-through to underlying button and production menu restore");

            Save(window, Path.Combine(root, "final-window.png"));
            var timer = (DispatcherTimer)Field(typeof(FloatingSubtitleWindow), "_pageTimer").GetValue(window)!;
            var oldText = vm.FormattedSubtitleText;
            manager.CloseWindow();
            await FlushAsync();
            Check(!timer.IsEnabled && !manager.IsWindowOpen && !window.IsVisible, "关闭未停止计时器/清理窗口状态");
            manager.UpdateSubtitle("关闭后的迟到文字");
            Check(vm.FormattedSubtitleText == oldText, "关闭后迟到更新仍写入旧 VM");
            manager.OpenWindow();
            Check(manager.IsWindowOpen && !ReferenceEquals(Field(typeof(FloatingSubtitleManager), "_window").GetValue(manager), window),
                "重开复用了关闭的窗口");
            manager.CloseWindow();
            Console.WriteLine("PASS close cleanup, late update exclusion and reopen");
            File.WriteAllText(Path.Combine(root, "result.txt"), "PASS: production floating subtitle desktop verification; controlled text, no cloud/audio request.");
            Console.WriteLine("FLOATING_SUBTITLE_EXIT=0");
        }
        finally
        {
            manager.CloseWindow();
            underlay.Close();
            owner.Close();
        }
    }

    private static void Save(Window window, string path)
    {
        var scale = window.RenderScaling;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
            (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(path);
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name, PrivateInstance)
        ?? throw new MissingFieldException(type.Name, name);
    private static T Value<T>(object target, string name) => (T)Field(target.GetType(), name).GetValue(target)!;
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.1;
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
    private static Button IconButton(Window window, string icon) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => button.GetVisualDescendants().OfType<Icon>().Any(value => value.Value == icon));
    private static async Task FlushAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        await Task.Delay(100);
    }
    private static async Task UntilAsync(Func<bool> ready, string error, int seconds)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(50);
        Check(ready(), error);
    }
    private static async Task ClickAsync(Control control)
    {
        var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
        Check(SetCursorPos(point.X, point.Y), "无法移动系统鼠标");
        await Task.Delay(220); // 等待生产悬停工具条淡入和原生命中测试生效。
        GetCursorPos(out var cursor);
        var top = TopLevel.GetTopLevel(control)!;
        var clientPoint = top.PointToClient(new PixelPoint(cursor.X, cursor.Y));
        var visualHit = top.InputHitTest(clientPoint);
        Console.WriteLine($"NATIVE_CLICK requested={point}, cursor={cursor.X},{cursor.Y}, target={top.TryGetPlatformHandle()?.Handle}, hit={WindowFromPoint(cursor)}, foreground={GetForegroundWindow()}, over={control.IsPointerOver}, enabled={control.IsEffectivelyEnabled}, hitTest={control.IsHitTestVisible}, bounds={control.Bounds}, scale={top.RenderScaling}, client={clientPoint}, visualHit={visualHit?.GetType().Name}");
        await MouseClickAsync(point);
    }
    private static async Task MouseClickAsync(PixelPoint point)
    {
        Check(SetCursorPos(point.X, point.Y), "无法定位系统鼠标");
        Send(new[] { Mouse(2), Mouse(4) });
        await FlushAsync();
    }
    private static async Task DragAsync(PixelPoint start, PixelPoint end)
    {
        Check(SetCursorPos(start.X, start.Y), "无法定位拖动起点");
        await Task.Delay(100);
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        NativeInput Move(PixelPoint point) => new()
        {
            Data = new InputUnion { Mouse = new MouseInput
            {
                X = (int)Math.Round((point.X - left) * 65535d / (width - 1)),
                Y = (int)Math.Round((point.Y - top) * 65535d / (height - 1)), Flags = 0xE001
            } }
        };
        // BeginMoveDrag/BeginResizeDrag 会进入原生消息循环，后台分阶段送入真实输入。
        await Task.Run(async () =>
        {
            Send(new[] { Mouse(2) });
            try
            {
                await Task.Delay(150);
                Send(new[] { Move(new PixelPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2)) });
                await Task.Delay(100);
                Send(new[] { Move(end) });
                await Task.Delay(100);
            }
            finally { Send(new[] { Mouse(4) }); }
        });
        await FlushAsync();
    }
    private static Task KeyAsync(ushort modifier, ushort key) => KeyAsync(new[] { modifier }, key);
    private static async Task KeyAsync(ushort[] modifiers, ushort key)
    {
        var inputs = modifiers.Select(value => Keyboard(value, false)).ToList();
        inputs.Add(Keyboard(key, false));
        inputs.Add(Keyboard(key, true));
        inputs.AddRange(modifiers.Reverse().Select(value => Keyboard(value, true)));
        Send(inputs.ToArray());
        await FlushAsync();
    }
    private static NativeInput Mouse(uint flags) => new() { Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };
    private static NativeInput Keyboard(ushort key, bool up) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } }
    };
    private static void Send(NativeInput[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        Check(sent == inputs.Length, $"SendInput={sent}/{inputs.Length}, Win32={Marshal.GetLastWin32Error()}");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}