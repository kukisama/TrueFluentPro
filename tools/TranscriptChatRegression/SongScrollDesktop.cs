using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TrueFluentPro;

// 只扩展旧 native helper 的可访问入口，不改旧专项断言。
internal static partial class WindowChromeRegression
{
    internal static async Task ScrollForegroundAsync(MainWindow window, Control drag, CancellationToken token)
    {
        window.Activate(); await Dispatcher.UIThread.InvokeAsync(() => {}, DispatcherPriority.Background);
        if (GetForegroundWindow() != window.TryGetPlatformHandle()!.Handle)
        {
            var topmost = window.Topmost;
            try
            {
                window.Topmost = true; await Task.Delay(100,token);
                var point = Center(drag);
                Check(WindowFromPoint(new NativePoint { X=point.X,Y=point.Y }) == window.TryGetPlatformHandle()!.Handle,"前台目标不属于本次 HWND。");
                await FloatingSubtitleRegression.MouseClickAsync(point);
                await UntilAsync(() => GetForegroundWindow() == window.TryGetPlatformHandle()!.Handle,
                    $"测试窗未获得前台：own={window.TryGetPlatformHandle()!.Handle};foreground={GetForegroundWindow()}。",token);
            }
            finally { window.Topmost = topmost; }
        }
        await UntilAsync(() => GetForegroundWindow() == window.TryGetPlatformHandle()!.Handle,"测试窗未获得前台。",token);
    }
    internal static async Task ScrollClickAsync(MainWindow window, Control control, CancellationToken token)
    {
        var point=Center(control); CheckInputTarget(window,control,point); Check(SetCursorPos(point.X,point.Y),"无法定位鼠标");
        await Task.Delay(100,token); point=Center(control); CheckInputTarget(window,control,point);
        var clicks=0;
        void Observe(object? sender,Avalonia.Interactivity.RoutedEventArgs e){if(ReferenceEquals(e.Source,control))clicks++;}
        var button=control as Button;
        if(button!=null)button.Click+=Observe;
        try
        {
            await FloatingSubtitleRegression.MouseClickAsync(point);
            if(button!=null)
            {
                await UntilAsync(()=>clicks>0,"原生输入没有产生目标按钮Click，不重试点击。",token);
                Check(clicks==1,"一次原生输入产生了重复Click。");
                Console.WriteLine($"NATIVE_TARGET_CLICK name={control.Name};count={clicks};point={point}");
            }
        }
        finally{if(button!=null)button.Click-=Observe;}
        token.ThrowIfCancellationRequested();
    }
    internal static async Task ScrollPopupClickAsync(MainWindow window, Control control, CancellationToken token)
    {
        var top=TopLevel.GetTopLevel(control)??throw new InvalidOperationException("菜单未连接自身PopupRoot");
        var point=Center(control); var handle=top.TryGetPlatformHandle()!.Handle;
        Check(GetForegroundWindow()==window.TryGetPlatformHandle()!.Handle&&WindowFromPoint(new NativePoint{X=point.X,Y=point.Y})==handle,"菜单命中不属于本次原生弹层");
        Check(control.IsEffectivelyVisible&&control.IsEffectivelyEnabled,"菜单不可用");
        await FloatingSubtitleRegression.MouseClickAsync(point);token.ThrowIfCancellationRequested();
    }
    internal static void ScrollCapture(MainWindow window, string path)
    {
        Check(GetForegroundWindow() == window.TryGetPlatformHandle()!.Handle,"不得截其他窗口冒充测试。");
        var origin = window.PointToScreen(default);
        using var pixels = ScreenPixels(origin.X,origin.Y,(int)Math.Round(window.ClientSize.Width*window.RenderScaling),(int)Math.Round(window.ClientSize.Height*window.RenderScaling));
        pixels.Save(path);
    }
    internal static void ScrollHover(MainWindow window, Control control)
    { var p=Center(control); CheckInputTarget(window,control,p); Check(SetCursorPos(p.X,p.Y),"真实hover定位失败。"); }
    internal static async Task ScrollStateAsync(MainWindow window, WindowState state, CancellationToken token)
        => await StateAsync(window,window.TryGetPlatformHandle()!.Handle,state,token);
    internal static void ScrollRestore(MainWindow window) => ShowWindow(window.TryGetPlatformHandle()!.Handle,9);
    internal static void ScrollStyle(MainWindow window, Action<string> log) => CheckStyle(window,window.TryGetPlatformHandle()!.Handle,log);
    internal static async Task ScrollDoubleClickAsync(MainWindow window, Control drag, CancellationToken token)
    {
        await Task.Delay((int)GetDoubleClickTime()+30,token); var point=Center(drag); CheckInputTarget(window,drag,point);
        await FloatingSubtitleRegression.MouseClickAsync(point); await FloatingSubtitleRegression.MouseClickAsync(point);
    }
    internal static async Task ScrollResizeAsync(MainWindow window,CancellationToken token,Action<string> log)
    { await ResizeNativeEdgeAsync(window,window.TryGetPlatformHandle()!.Handle,window.Screens.ScreenFromWindow(window)!,token,log); }
    internal static void ScrollCaptureTop(MainWindow window,string path,Action<string> log)
    {
        var hwnd=window.TryGetPlatformHandle()!.Handle;
        Check(DwmGetWindowAttribute(hwnd,9,out NativeRect rect,System.Runtime.InteropServices.Marshal.SizeOf<NativeRect>())==0,"无法读取DWM边缘");
        var client=window.PointToScreen(default);
        using var image=ScreenPixels(rect.Left,rect.Top,rect.Width,client.Y-rect.Top+20);image.Save(path);
        using var buffer=image.Lock();var white=0;var count=0;
        for(var y=0;y<client.Y-rect.Top;y++)for(var x=rect.Width/3;x<rect.Width*2/3;x++)
        {var rgb=System.Runtime.InteropServices.Marshal.ReadInt32(buffer.Address,y*buffer.RowBytes+x*4);if(((rgb>>16)&255)>=248&&((rgb>>8)&255)>=248&&(rgb&255)>=248)white++;count++;}
        Check(white==0,"DWM上沿出现白条");log($"DWM_TOP nativeRows={client.Y-rect.Top};whitePixels={white}/{count};snapshot={path}");
    }
}