using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using TrueFluentPro.ViewModels;

namespace TrueFluentPro.Controls;

/// <summary>正常选择编辑，按听写请求同步片段光标；用户移动优先，不锁全文末尾。</summary>
public sealed class VoiceInputTextBox : TextBox
{
    public static readonly StyledProperty<ChatInputCursor?> CursorStateProperty =
        AvaloniaProperty.Register<VoiceInputTextBox, ChatInputCursor?>(nameof(CursorState));

    private bool _movingCaret;
    private bool _caretUpdateQueued;
    private TextPresenter? _presenter;
    private bool _changingText;
    private bool _handlingGesture;
    private bool _positionPending;
    private long _positionRevision;
    private ChatInputCursor? _subscribedCursor;
    protected override Type StyleKeyOverride => typeof(TextBox);

    public VoiceInputTextBox()
    {
        TextChanged += (_, _) => QueueCaretUpdate();
        SizeChanged += (_, _) => QueueCaretUpdate();
    }

    public ChatInputCursor? CursorState
    {
        get => GetValue(CursorStateProperty);
        set => SetValue(CursorStateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        var wasChangingText = _changingText;
        _changingText |= change.Property == TextProperty;
        try { base.OnPropertyChanged(change); }
        finally { _changingText = wasChangingText; }
        if (change.Property == CursorStateProperty)
        {
            SubscribeCursor();
        }
        else if (change.Property == TextProperty)
        {
            QueueCaretUpdate();
        }
        else if ((change.Property == CaretIndexProperty || change.Property == SelectionStartProperty
            || change.Property == SelectionEndProperty) && !_changingText && !_handlingGesture)
        {
            CaptureSelection();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeCursor();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedCursor != null) _subscribedCursor.PositionRequested -= OnPositionRequested;
        _subscribedCursor = null;
        _positionPending = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeCursor()
    {
        if (ReferenceEquals(_subscribedCursor, CursorState)) return;
        if (_subscribedCursor != null) _subscribedCursor.PositionRequested -= OnPositionRequested;
        _subscribedCursor = CursorState;
        if (_subscribedCursor != null) _subscribedCursor.PositionRequested += OnPositionRequested;
        _positionPending = false;
    }

    private void OnPositionRequested()
    {
        _positionPending = true;
        _positionRevision = CursorState?.Revision ?? 0;
        QueueCaretUpdate();
    }

    private void CaptureSelection()
    {
        if (_movingCaret || _changingText || CursorState?.IsApplyingVoiceText == true) return;
        _positionPending = false;
        CursorState?.UpdateFromUser(CaretIndex, SelectionStart, SelectionEnd);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_presenter != null) _presenter.PropertyChanged -= OnPresenterPropertyChanged;
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        if (_presenter != null) _presenter.PropertyChanged += OnPresenterPropertyChanged;
        QueueCaretUpdate();
    }

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextPresenter.TextProperty || e.Property == TextPresenter.CaretIndexProperty
            || e.Property == TextPresenter.PreeditTextProperty)
            QueueCaretUpdate();
    }

    private void QueueCaretUpdate()
    {
        if (!_positionPending || _movingCaret || _caretUpdateQueued) return;
        _caretUpdateQueued = true;
        // TextBox.Text 与模板中的 TextPresenter.Text 不是同时更新。
        // 合并到当前文本/绑定更新完成后处理，避免按旧文本长度约束绘制光标。
        Dispatcher.UIThread.Post(() =>
        {
            _caretUpdateQueued = false;
            ApplyRequestedPosition();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _handlingGesture = true;
        try { base.OnKeyDown(e); }
        finally { _handlingGesture = false; }
        CaptureSelection();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        _handlingGesture = true;
        try { base.OnTextInput(e); }
        finally { _handlingGesture = false; }
        CaptureSelection();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _handlingGesture = true;
        try { base.OnPointerPressed(e); }
        finally { _handlingGesture = false; }
        CaptureSelection();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _handlingGesture = true;
        try { base.OnPointerReleased(e); }
        finally { _handlingGesture = false; }
        CaptureSelection();
    }

    private void ApplyRequestedPosition()
    {
        var cursor = CursorState;
        if (!_positionPending || _movingCaret || cursor == null || cursor.Revision != _positionRevision) return;
        // 不干扰输入法尚未提交的组合文字；提交后由 Text/Preedit 变化再次对齐。
        if (_presenter != null && (!string.IsNullOrEmpty(_presenter.PreeditText)
            || !string.Equals(_presenter.Text, Text, StringComparison.Ordinal))) return;
        _movingCaret = true;
        try
        {
            var length = Text?.Length ?? 0;
            var caret = Math.Clamp(cursor.CaretIndex, 0, length);
            var start = Math.Clamp(cursor.SelectionStart, 0, length);
            var end = Math.Clamp(cursor.SelectionEnd, 0, length);
            SetCurrentValue(CaretIndexProperty, caret);
            SetCurrentValue(SelectionStartProperty, start);
            SetCurrentValue(SelectionEndProperty, end);
            if (_presenter != null)
            {
                // 逻辑 CaretIndex 已等于 end 时不会再触发属性通知，但绘制命中仍可能滞后。
                // 使用公开的定位方法重新计算实际字符命中和光标矩形，而非只改逻辑数值。
                _presenter.MoveCaretToTextPosition(caret, trailingEdge: true);
                _presenter.SetCurrentValue(TextPresenter.SelectionStartProperty, start);
                _presenter.SetCurrentValue(TextPresenter.SelectionEndProperty, end);
            }
            _positionPending = false;
        }
        finally { _movingCaret = false; }
    }
}