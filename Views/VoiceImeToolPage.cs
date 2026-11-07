using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using TrueFluentPro.Services;

namespace TrueFluentPro.Views;

internal enum VoiceImeToolTab { Settings = 1, Usage = 2, Language = 3 }

/// <summary>输入法工具页；宿主在首帧渲染后初始化，关闭时释放，不另开子窗口。</summary>
internal abstract class VoiceImeToolPage : UserControl, IDisposable
{
    internal abstract Task InitializeAsync();
    internal virtual bool CanClose => true;
    internal virtual Task PrepareToLeaveAsync() => Task.CompletedTask;
    public abstract void Dispose();

    // “完成”只请求宿主关闭；统一 Shell 与兼容窗口各自处理窗口生命周期。
    internal event Action? CloseRequested;
    protected void RequestClose() => CloseRequested?.Invoke();

    internal event Action<VoiceImeToolTab>? NavigationRequested;
    protected void RequestNavigation(VoiceImeToolTab kind) => NavigationRequested?.Invoke(kind);

    // 与 UI 宿主同等的首帧等待；兼容窗口及源码链接页面不必依赖宿主实现。
    internal static async Task RenderedAsync(Window window, CancellationToken token = default)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        window.UpdateLayout();
        var visual = ElementComposition.GetElementVisual(window) ?? throw new InvalidOperationException("窗口尚未连接合成器。");
        await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered
            .WaitAsync(TimeSpan.FromSeconds(8), token), DispatcherPriority.Background);
    }

    /// <summary>仅兼容窗口使用：把同一页的正文、页脚移入原三行布局，页面仍是唯一业务控制器。</summary>
    internal static Grid Wrap(Window window, VoiceImeToolPage page, string title, string subtitle, Control? aside = null)
    {
        var pageRoot = (Grid)page.Content!;
        var scroll = pageRoot.Children[0];
        var footer = pageRoot.Children[1];
        pageRoot.Children.Clear();
        window.DataContext = page.DataContext;

        // 页面不再位于窗口视觉树中，局部样式也随正文迁移，避免丢失报表行样式。
        while (page.Styles.Count > 0)
        {
            var style = page.Styles[0];
            page.Styles.RemoveAt(0);
            window.Styles.Add(style);
        }

        var root = new Grid { RowDefinitions = new("Auto,*,Auto") };
        root.Children.Add(VoiceImeWindowDesign.Header(window, title, subtitle, aside));
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }
}