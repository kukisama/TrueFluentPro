using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using TrueFluentPro.Models;
using TrueFluentPro.Services;

namespace TrueFluentPro.ViewModels;

/// <summary>创作工坊只保留一个听写任务，结果始终写入启动时的会话，不自动发送。</summary>
public sealed class ChatVoiceInputViewModel : ViewModelBase, IDisposable
{
    private readonly IChatVoiceInputFactory? _factory;
    private readonly Func<AzureSpeechConfig> _configProvider;
    private readonly Func<MediaSessionViewModel?> _currentSession;
    private VoiceRun? _run;
    private bool _disposed;
    private bool _isActive;
    private bool _isRecording;
    private bool _isStopping;
    private string _statusText = "";
    private string _hotkeyHint = "F8 开始/停止语音输入";
    private double _audioLevel;

    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public bool IsRecording { get => _isRecording; private set => SetProperty(ref _isRecording, value); }
    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (SetProperty(ref _isStopping, value))
                ((RelayCommand)ToggleCommand).RaiseCanExecuteChanged();
        }
    }
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);
    public double AudioLevel
    {
        get => _audioLevel;
        private set
        {
            if (SetProperty(ref _audioLevel, value))
            {
                OnPropertyChanged(nameof(LevelBarHeight));
                OnPropertyChanged(nameof(LevelSideBarHeight));
                OnPropertyChanged(nameof(MicrophoneScale));
            }
        }
    }
    public double LevelBarHeight => 4 + AudioLevel * 16;
    public double LevelSideBarHeight => 3 + AudioLevel * 10;
    public double MicrophoneScale => 1 + AudioLevel * 0.14;
    public string HotkeyHint { get => _hotkeyHint; set => SetProperty(ref _hotkeyHint, value); }
    public ICommand ToggleCommand { get; }

    public ChatVoiceInputViewModel(IChatVoiceInputFactory? factory, Func<AzureSpeechConfig> configProvider,
        Func<MediaSessionViewModel?> currentSession)
    {
        _factory = factory;
        _configProvider = configProvider;
        _currentSession = currentSession;
        ToggleCommand = new RelayCommand(_ => _ = ToggleAsync(), _ => !_disposed && !IsStopping);
    }

    public async Task ToggleAsync()
    {
        if (_disposed || IsStopping) return;
        if (_run != null)
        {
            await StopAsync();
            return;
        }
        var target = _currentSession();
        if (target == null)
        {
            StatusText = "请先新建或选择聊天会话。";
            return;
        }
        if (target.IsOptimizingPrompt)
        {
            StatusText = "提示词正在优化，请完成后再开始语音输入。";
            return;
        }
        if (_factory == null)
        {
            StatusText = "语音输入服务尚未就绪，请重新打开应用。";
            return;
        }

        VoiceRun run;
        try { run = new VoiceRun(_factory.Create(_configProvider()), target); }
        catch
        {
            StatusText = "无法启动语音输入，请检查实时翻译的语音资源配置。";
            return;
        }
        _run = run;
        IsActive = true;
        target.IsVoiceInputActive = true;
        StatusText = "";
        AudioLevel = 0;
        run.OnText = (text, final) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_run, run)) return;
            run.Draft.Apply(text, final);
        });
        run.OnFailure = message => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_run, run)) return;
            run.Failed = true;
            StatusText = message;
            _ = StopAsync();
        });
        run.OnLevel = level => Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(_run, run) && IsRecording)
                AudioLevel = Math.Clamp(level, 0, 1);
        });
        run.Session.TextRecognized += run.OnText;
        run.Session.Failed += run.OnFailure;
        run.Session.AudioLevelChanged += run.OnLevel;
        try
        {
            await run.Session.StartAsync(run.StartCancellation.Token);
            if (ReferenceEquals(_run, run) && run.StopTask == null && !run.Failed)
            {
                IsRecording = true;
            }
        }
        catch (OperationCanceledException) when (run.StopTask != null || _disposed) { }
        catch
        {
            if (ReferenceEquals(_run, run) && !run.Failed && run.StopTask == null)
            {
                run.Failed = true;
                StatusText = "语音输入启动失败，请检查语音资源、登录状态及麦克风权限。";
            }
            if (ReferenceEquals(_run, run)) await StopAsync();
        }
    }

    public Task StopAsync()
    {
        var run = _run;
        if (run == null) return Task.CompletedTask;
        // 立即结束本次输入，先隔离所有迟到回调，再在后台释放设备/网络。
        _run = null;
        run.Session.TextRecognized -= run.OnText;
        run.Session.Failed -= run.OnFailure;
        run.Session.AudioLevelChanged -= run.OnLevel;
        run.Draft.Detach();
        run.Target.IsVoiceInputActive = false;
        IsRecording = false;
        IsActive = false;
        AudioLevel = 0;
        if (!run.Failed) StatusText = "";
        run.StartCancellation.Cancel();
        run.StopTask = Task.Run(() => StopCoreAsync(run));
        return Task.CompletedTask;
    }

    private async Task StopCoreAsync(VoiceRun run)
    {
        try
        {
            await run.Session.StopAsync();
            await run.Session.DisposeAsync();
        }
        catch
        {
            // 输入已经结束，旧会话的清理结果不得影响下一次听写或重新亮起按钮。
        }
        finally
        {
            run.StartCancellation.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ((RelayCommand)ToggleCommand).RaiseCanExecuteChanged();
        _ = StopAsync();
    }

    private sealed class VoiceRun
    {
        public readonly IChatVoiceInputSession Session;
        public readonly MediaSessionViewModel Target;
        public readonly VoiceDraft Draft;
        public readonly CancellationTokenSource StartCancellation = new();
        public Action<string, bool>? OnText;
        public Action<string>? OnFailure;
        public Action<double>? OnLevel;
        public Task? StopTask;
        public bool Failed;
        public VoiceRun(IChatVoiceInputSession session, MediaSessionViewModel target)
        {
            Session = session;
            Target = target;
            Draft = new VoiceDraft(target);
        }
    }

    /// <summary>当前句固定范围修订，用户移动的锚点供下一句使用；不停止听写或锁全文末尾。</summary>
    private sealed class VoiceDraft
    {
        private readonly MediaSessionViewModel _target;
        private readonly ChatInputCursor _cursor;
        private string _snapshot;
        private int _start;
        private int _length;
        private string? _owned;
        private bool _userEdited;
        private bool _hasNextAnchor;
        private int _nextCaret;
        private int _nextStart;
        private int _nextEnd;

        public VoiceDraft(MediaSessionViewModel target)
        {
            _target = target;
            _cursor = target.InputCursor;
            _snapshot = target.PromptText;
            SelectCurrentPosition();
            _cursor.UserMoved += OnUserMoved;
        }

        public void Detach() => _cursor.UserMoved -= OnUserMoved;

        private void SelectCurrentPosition()
        {
            var length = _target.PromptText.Length;
            _start = _cursor.HasPosition
                ? Math.Clamp(Math.Min(_cursor.SelectionStart, _cursor.SelectionEnd), 0, length) : length;
            var end = _cursor.HasPosition
                ? Math.Clamp(Math.Max(_cursor.SelectionStart, _cursor.SelectionEnd), _start, length) : length;
            _length = end - _start;
            _userEdited = false;
        }

        private void OnUserMoved()
        {
            if (_cursor.IsApplyingVoiceText) return;
            RebaseEdits(_target.PromptText);
            if (_owned == null)
            {
                SelectCurrentPosition();
                _hasNextAnchor = false;
                return;
            }
            _nextCaret = _cursor.CaretIndex;
            _nextStart = _cursor.SelectionStart;
            _nextEnd = _cursor.SelectionEnd;
            _hasNextAnchor = _userEdited || _nextStart != _start + _length || _nextEnd != _nextStart;
        }

        public void Apply(string text, bool final)
        {
            var prompt = _target.PromptText;
            RebaseEdits(prompt);
            // 尚未拥有识别片段时，以当前选区为准；整段文本替换不能污染下一句状态。
            if (_owned == null) SelectCurrentPosition();
            if (!_userEdited && (_owned != null || !string.IsNullOrWhiteSpace(text)))
            {
                if (_start + _length > prompt.Length || (_owned != null
                    && !prompt.AsSpan(_start, _length).SequenceEqual(_owned.AsSpan())))
                {
                    _userEdited = true;
                }
                else
                {
                    var oldEnd = _start + _length;
                    var delta = text.Length - _length;
                    if (_hasNextAnchor)
                    {
                        _nextCaret = MapPosition(_nextCaret, _start, oldEnd, text.Length);
                        _nextStart = MapPosition(_nextStart, _start, oldEnd, text.Length);
                        _nextEnd = MapPosition(_nextEnd, _start, oldEnd, text.Length);
                    }
                    _cursor.IsApplyingVoiceText = true;
                    try
                    {
                        _target.PromptText = prompt.Remove(_start, _length).Insert(_start, text);
                        _owned = text;
                        _length += delta;
                        _snapshot = _target.PromptText;
                        if (_hasNextAnchor)
                            _cursor.RequestPosition(_nextCaret, _nextStart, _nextEnd);
                        else
                        {
                            var end = _start + _length;
                            _cursor.RequestPosition(end, end, end);
                        }
                    }
                    finally { _cursor.IsApplyingVoiceText = false; }
                }
            }
            if (final)
            {
                if (_hasNextAnchor)
                {
                    _start = Math.Clamp(Math.Min(_nextStart, _nextEnd), 0, _target.PromptText.Length);
                    _length = Math.Clamp(Math.Max(_nextStart, _nextEnd), _start, _target.PromptText.Length) - _start;
                }
                else if (_userEdited)
                    SelectCurrentPosition();
                else
                {
                    _start += _length;
                    _length = 0;
                }
                _owned = null;
                _userEdited = false;
                _hasNextAnchor = false;
            }
        }

        private void RebaseEdits(string prompt)
        {
            if (string.Equals(prompt, _snapshot, StringComparison.Ordinal)) return;
            var prefix = 0;
            while (prefix < prompt.Length && prefix < _snapshot.Length && prompt[prefix] == _snapshot[prefix]) prefix++;
            var suffix = 0;
            while (suffix < prompt.Length - prefix && suffix < _snapshot.Length - prefix
                && prompt[^(suffix + 1)] == _snapshot[^(suffix + 1)]) suffix++;
            var oldEnd = _snapshot.Length - suffix;
            var replacementLength = prompt.Length - prefix - suffix;
            if (oldEnd <= _start)
                _start += prompt.Length - _snapshot.Length;
            else if (prefix < _start + _length)
                _userEdited = true; // 手动改写当前片段时，以用户文字为准，不覆盖。
            if (_hasNextAnchor)
            {
                _nextCaret = MapPosition(_nextCaret, prefix, oldEnd, replacementLength);
                _nextStart = MapPosition(_nextStart, prefix, oldEnd, replacementLength);
                _nextEnd = MapPosition(_nextEnd, prefix, oldEnd, replacementLength);
            }
            _start = Math.Clamp(_start, 0, prompt.Length);
            _snapshot = prompt;
        }

        private static int MapPosition(int position, int start, int oldEnd, int newLength)
        {
            if (position <= start) return position;
            if (position >= oldEnd) return position + newLength - (oldEnd - start);
            return start + newLength;
        }
    }
}