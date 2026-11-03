using System;

namespace TrueFluentPro.ViewModels;

/// <summary>输入框与听写共享的会话级插入位置；区分用户移动和识别结果请求，避免抢光标。</summary>
public sealed class ChatInputCursor
{
    public int CaretIndex { get; private set; }
    public int SelectionStart { get; private set; }
    public int SelectionEnd { get; private set; }
    public bool HasPosition { get; private set; }
    public long Revision { get; private set; }
    public bool IsApplyingVoiceText { get; internal set; }
    public event Action? UserMoved;
    public event Action? PositionRequested;

    public void UpdateFromUser(int caret, int start, int end)
    {
        if (HasPosition && CaretIndex == caret && SelectionStart == start && SelectionEnd == end) return;
        CaretIndex = Math.Max(0, caret);
        SelectionStart = Math.Max(0, start);
        SelectionEnd = Math.Max(0, end);
        HasPosition = true;
        Revision++;
        UserMoved?.Invoke();
    }

    public void RequestPosition(int caret, int start, int end)
    {
        CaretIndex = Math.Max(0, caret);
        SelectionStart = Math.Max(0, start);
        SelectionEnd = Math.Max(0, end);
        HasPosition = true;
        PositionRequested?.Invoke();
    }
}