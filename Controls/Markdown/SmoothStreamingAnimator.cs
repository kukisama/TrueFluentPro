using System;
using System.Threading;
using Avalonia.Threading;

namespace TrueFluentPro.Controls.Markdown;

/// <summary>
/// 字符级平滑流式动画器。
/// 参考 Cherry Studio 的 useSmoothStream 实现：
/// AI 返回的 token 不直接渲染，先进入 pendingChars 队列，
/// 通过 DispatcherTimer 每帧取若干字符追加到显示文本，实现打字机效果。
/// chunkSize 动态调节（积压多时加速、少时减速），视觉上比直接 80ms flush 更平滑。
/// </summary>
public sealed class SmoothStreamingAnimator : IDisposable
{
    private readonly Action<string> _onTextUpdated;
    private readonly object _lock = new();

    /// <summary>尚未渲染到界面的字符缓冲</summary>
    private string _pendingBuffer = "";

    /// <summary>当前已渲染到界面的文本</summary>
    private string _displayedText = "";

    /// <summary>动画定时器</summary>
    private DispatcherTimer? _timer;

    /// <summary>是否已被标记为流结束（需要把剩余 pending 全部刷出）</summary>
    private bool _streamEnded;

    // ── 动画参数 ─────────────────────────────────────────

    /// <summary>定时器间隔：200ms（5fps），平衡流畅度与渲染压力</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>每 tick 最大刷出字符数，防止大段代码块一次性渲染导致 UI 卡死</summary>
    private const int MaxCharsPerTick = 500;

    /// <summary>
    /// 创建平滑流式动画器。
    /// </summary>
    /// <param name="onTextUpdated">每次文本更新时的回调，接收当前已渲染的完整文本。
    /// 回调保证在 UI 线程上执行。</param>
    public SmoothStreamingAnimator(Action<string> onTextUpdated)
    {
        _onTextUpdated = onTextUpdated;
    }

    /// <summary>
    /// 追加来自 AI 流式响应的新 token。可在任意线程调用。
    /// </summary>
    public void AppendToken(string token)
    {
        if (string.IsNullOrEmpty(token))
            return;

        lock (_lock)
        {
            _pendingBuffer += token;
        }

        EnsureTimerRunning();
    }

    /// <summary>
    /// 标记流结束。动画器会将剩余 pending 字符全部刷出后停止定时器。
    /// </summary>
    public void EndStream()
    {
        lock (_lock)
        {
            _streamEnded = true;
        }
        EnsureTimerRunning();
    }

    /// <summary>
    /// 重置状态，准备下一次流式会话。
    /// </summary>
    public void Reset()
    {
        StopTimer();
        lock (_lock)
        {
            _pendingBuffer = "";
            _displayedText = "";
            _streamEnded = false;
        }
    }

    /// <summary>获取当前已渲染的文本</summary>
    public string DisplayedText
    {
        get { lock (_lock) return _displayedText; }
    }

    public void Dispose()
    {
        StopTimer();
    }

    // ── 定时器驱动 ───────────────────────────────────────

    private void EnsureTimerRunning()
    {
        if (_timer != null)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Render, OnTick);
            _timer.Start();
        });
    }

    private void StopTimer()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer?.Stop();
            _timer = null;
        });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        string newDisplayed;

        lock (_lock)
        {
            if (_pendingBuffer.Length == 0)
            {
                if (_streamEnded)
                {
                    StopTimer();
                }
                return;
            }

            // 每 tick 最多刷出 MaxCharsPerTick 字符，保持 5fps 节奏不因大代码块卡死
            // 流结束后直接全部刷出
            int take = _streamEnded
                ? _pendingBuffer.Length
                : Math.Min(_pendingBuffer.Length, MaxCharsPerTick);

            _displayedText += _pendingBuffer[..take];
            _pendingBuffer = _pendingBuffer[take..];
            newDisplayed = _displayedText;
        }

        _onTextUpdated(newDisplayed);
    }
}
