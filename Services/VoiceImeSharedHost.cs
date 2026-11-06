using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services;

public static partial class VoiceImeHost
{
    private static async Task<int> RunSharedAsync()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new IOException();
            using var process = Process.GetCurrentProcess();
            var name = $"TrueFluentPro.VoiceIme.Shared.{process.SessionId}.{sid}";
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TrueFluentPro", "VoiceIme");
            var ownerPath = Path.Combine(directory, "owner.lock");
            VoiceImeMaintenance.GuardPath(ownerPath);
            using var lifetime = new CancellationTokenSource();
            VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime);
            Directory.CreateDirectory(directory);
            FileStream owner;
            try { owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return 0; } // 已有 owner，由 native 连接同一个确定性 pipe
            using (owner)
            {
                VoiceImeMaintenance.Check(VoiceImeMaintenance.GatePath, lifetime);
                VoiceImeMaintenance.RecordHost(directory); // Idle 也必须被维护发现
                var resolver = new SpeechResourceRuntimeResolver(new ModelRuntimeResolver());
                var tokens = new AzureTokenProviderStore();
                var authentication = new SharedAuthentication(process.SessionId, process.MainModule!.FileName);
                using var configCache = new RuntimeConfigurationCache();
                await using var usage = new VoiceImeUsageService(() => new SqliteDbService(
                    Path.Combine(PathManager.Instance.AppDataPath, "truefluentpro.db")),
                    process.SessionId.ToString(CultureInfo.InvariantCulture));
                await using var session = new MainSharedSession(name, VoiceImeMaintenance.GatePath,
                    authentication.Authenticate, authentication.Foreground, configCache.GetAsync,
                    config => CreateVoiceInputSession(config, new ChatVoiceInputFactory(resolver, new RealtimeConnectionSpecResolver(), tokens,
                        useConfiguredLanguage: true, endSilenceTimeoutSeconds: config.VoiceIme.EndSilenceTimeoutSeconds), tokens),
                    Path.Combine(directory, "microphone.lock"), callback => new VoiceImeToolbar(callback), lifetime.Token,
                    enablePrewarmRouting: true, usage: usage);
                return await session.RunAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return 0; }
        catch { return 1; } // 不回显云 URL、密钥、识别正文或认证详情
    }

    // 明确 fixture seam；不读取用户 owner/config、不加载 native，不接受任何生产开关。
    internal static async Task<int> RunSharedFixtureAsync(string name, string gate,
        Func<NamedPipeServerStream, uint, bool> authenticate, Func<NamedPipeServerStream, uint, bool> foreground,
        Func<Task<AzureSpeechConfig>> load, Func<AzureSpeechConfig, IChatVoiceInputSession> create,
        Action<Action<int, int>> attachUi, Action<bool, int, bool, bool> updateUi, Action closeUi,
        Task uiClosed, CancellationToken token, TimeProvider? timeProvider = null,
        VoiceImeUsageService? usage = null, Action<bool, bool>? updateHistory = null)
    {
        await using var session = new MainSharedSession(name, gate, authenticate, foreground, load, create,
            null, callback => new FixtureToolbar(callback, attachUi, updateUi, closeUi, uiClosed, updateHistory), token,
            timeProvider: timeProvider, usage: usage);
        return await session.RunAsync().ConfigureAwait(false);
    }

    private sealed class FixtureToolbar : IVoiceImeToolbar
    {
        private readonly Action<bool, int, bool, bool> _update;
        private readonly Action _close;
        private readonly Action<bool, bool>? _history;
        private int _closed;
        public FixtureToolbar(Action<int, int> callback, Action<Action<int, int>> attach,
            Action<bool, int, bool, bool> update, Action close, Task completion, Action<bool, bool>? history)
        { _update = update; _close = close; _history = history; Completion = completion; attach(callback); }
        public Task Ready => Task.CompletedTask;
        public Task Completion { get; }
        public void Update(bool visible, int state, bool stopped, bool error) => _update(visible, state, stopped, error);
        public void UpdateHistory(bool enabled, bool available) => _history?.Invoke(enabled, available);
        public void Close() { if (Interlocked.Exchange(ref _closed, 1) == 0) _close(); }
        public ValueTask DisposeAsync() { Close(); return new ValueTask(Completion); }
    }

    /// <summary>只协调本输入法的 owner、当前 peer/generation 与一轮音频；不持有目标 HWND/COM。</summary>
    private sealed class MainSharedSession : IAsyncDisposable
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly TimeSpan NoTextTimeout = TimeSpan.FromMinutes(1);
        private const string ErrorText = "语音输入已中断，请在主程序中检查语音资源和麦克风后重试。";
        private readonly string _name, _gate;
        private readonly string? _microphonePath;
        private readonly Func<NamedPipeServerStream, uint, bool> _authenticate;
        private readonly Func<NamedPipeServerStream, uint, bool> _foreground;
        private readonly Func<Task<AzureSpeechConfig>> _load;
        private readonly Func<AzureSpeechConfig, IChatVoiceInputSession> _create;
        private readonly CancellationTokenSource _lifetime;
        private readonly bool _enablePrewarmRouting;
        private readonly TimeProvider _timeProvider;
        private readonly VoiceImeUsageService? _usage;
        private readonly Channel<Action> _commands = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly HashSet<Peer> _peers = new(); // 仅 actor 访问
        private readonly SemaphoreSlim _slots = new(64);
        private readonly IVoiceImeToolbar _toolbar;
        private readonly List<Task> _connections = new(); // 仅 accept loop 访问
        private Task _actor = Task.CompletedTask, _accept = Task.CompletedTask, _monitor = Task.CompletedTask;
        private Task _released = Task.CompletedTask;
        private AudioRound? _audio;
        private PrewarmState? _prewarmed;
        private VoiceImeUiClient? _toolWindows;
        private Target? _target;
        private Target? _selection;
        private long _epoch;
        private long _targetRevision;
        private string _sentence = "";
        private int _boundary, _delivered;
        private char _status = 'I';
        private bool _visible, _stopped, _error;
        private int _disposed;

        public MainSharedSession(string name, string gate, Func<NamedPipeServerStream, uint, bool> authenticate,
            Func<NamedPipeServerStream, uint, bool> foreground, Func<Task<AzureSpeechConfig>> load,
            Func<AzureSpeechConfig, IChatVoiceInputSession> create, string? microphonePath,
            Func<Action<int, int>, IVoiceImeToolbar> toolbar, CancellationToken token,
            bool enablePrewarmRouting = false, TimeProvider? timeProvider = null, VoiceImeUsageService? usage = null)
        {
            _name = name; _gate = gate; _authenticate = authenticate; _foreground = foreground;
            _load = load; _create = create; _microphonePath = microphonePath;
            _enablePrewarmRouting = enablePrewarmRouting;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _usage = usage;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _toolbar = toolbar((kind, value) => Post(() =>
            {
                if (kind == 1) Toggle(); // 与 T 完全相同，不激活目标窗口
                else if (kind == 2 && value == 1) { _visible = true; UpdateUi(); MaybeTriggerPrewarm(); }
                else if (kind == 2 && value == 0) OtherIme(); // native ACTIVE 的独立实际依据
                else if (kind == 3) OpenSettingsWindow(); // 点击浮窗齿轮图标
                else if (kind == 5) _toolWindows?.Request(VoiceImeUiCommand.Usage);
                else if (kind == 6) { DiscardPrewarm(); _toolWindows?.Request(VoiceImeUiCommand.Language); }
                else if (kind == 4 && value is 0 or 1)
                {
                    // native 已确认同意；不读写整份语音资源 config，不乐观亮灯。
                    if (_usage != null) _ = _usage.SetHistoryAsync(value == 1);
                    UpdateHistory();
                }
            }));
            if (_usage != null) _usage.StatusChanged += UsageStatusChanged;
        }

        public async Task<int> RunAsync()
        {
            _actor = ActorAsync();
            _monitor = VoiceImeMaintenance.MonitorAsync(_gate, _lifetime, _lifetime.Token);
            try
            {
                VoiceImeMaintenance.Check(_gate, _lifetime);
                await _toolbar.Ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                Post(UpdateHistory);
                _accept = AcceptAsync();
                if (_enablePrewarmRouting)
                {
                    _toolWindows = new VoiceImeUiClient(_lifetime.Token);
                    _toolWindows.Request(VoiceImeUiCommand.Warm); // owner 就绪后后台预热；actor 不等待启动/IPC。
                }
                await Task.WhenAny(_accept, _actor, _toolbar.Completion,
                    Task.Delay(Timeout.Infinite, _lifetime.Token)).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return 0; }
            catch { return 1; }
            finally { _lifetime.Cancel(); }
        }

        private async Task ActorAsync()
        {
            try
            {
                await foreach (var command in _commands.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                    command();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { _lifetime.Cancel(); }
            finally
            {
                StopAudio("owner_exit");
                DiscardPrewarm();
                _visible = false;
                UpdateUi();
                foreach (var peer in _peers) peer.Close();
                _peers.Clear();
                _toolbar.Close();
            }
        }

        private void Post(Action action)
        {
            if (!_lifetime.IsCancellationRequested && !_commands.Writer.TryWrite(action)) _lifetime.Cancel();
        }

        private async Task SubmitAsync(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _commands.Writer.WriteAsync(() => { action(); done.TrySetResult(); }, _lifetime.Token).ConfigureAwait(false);
            await done.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    await _slots.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    NamedPipeServerStream? pipe = null;
                    try
                    {
                        pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                        _connections.RemoveAll(task => task.IsCompleted);
                        _connections.Add(ServeAsync(pipe));
                        pipe = null;
                    }
                    finally { if (pipe != null) { pipe.Dispose(); _slots.Release(); } }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { _lifetime.Cancel(); }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            Peer? peer = null;
            Task writer = Task.CompletedTask;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                handshake.CancelAfter(TimeSpan.FromSeconds(5));
                var hello = await ReadFrameAsync(pipe, handshake.Token).ConfigureAwait(false);
                if (hello.Kind != 'H' || !Decimal(hello.Text, out var thread) || thread is 0 or > uint.MaxValue
                    || !_authenticate(pipe, (uint)thread)) return;
                peer = new Peer(pipe, (uint)thread, _lifetime.Token);
                writer = WritePeerAsync(peer);
                await SubmitAsync(() => { _peers.Add(peer); Send(peer, _status, 0, ""); }).ConfigureAwait(false);
                while (!peer.Token.IsCancellationRequested)
                {
                    var frame = await ReadFrameAsync(pipe, peer.Token).ConfigureAwait(false);
                    if (frame.Kind is not ('L' or 'B' or 'U' or 'T' or 'D') || !Decimal(frame.Text, out var generation)) break;
                    await SubmitAsync(() => Command(peer, frame.Kind, generation)).ConfigureAwait(false);
                }
            }
            catch { } // EOF、认证/协议/写错误只关闭此 peer，绝不结束 owner/音频
            finally
            {
                if (peer != null)
                {
                    peer.Close();
                    try
                    {
                        await SubmitAsync(() =>
                        {
                            _peers.Remove(peer);
                            Unbind(peer, null);
                            if (_selection is { } selected && selected.Peer == peer && _audio == null)
                                OtherIme();
                        }).ConfigureAwait(false);
                    }
                    catch { }
                    try { await writer.ConfigureAwait(false); } catch { }
                    peer.Dispose();
                }
                pipe.Dispose();
                _slots.Release();
            }
        }

        private void Command(Peer peer, char kind, ulong generation)
        {
            if (!_peers.Contains(peer) || peer.Token.IsCancellationRequested) return;
            if (kind == 'U') { Unbind(peer, generation); return; }
            if (kind == 'D')
            {
                if (generation < peer.Generation) return;
                if ((_selection is { } selected && selected.Peer == peer && generation >= selected.Generation)
                    || (_selection == null && _visible)
                    || SafeForeground(peer))
                    OtherIme();
                return;
            }
            if (!SafeForeground(peer) || generation < peer.Generation) return;
            if (kind == 'B' && generation == 0) return;
            peer.Generation = generation;
            if (kind != 'B' && _target is { } bound && bound.Peer == peer && bound.Generation != generation)
                Unbind(peer, bound.Generation); // 新 L/T 不等于新 context 已经通过 Allowed/B
            switch (kind)
            {
                case 'L': _selection = new Target(peer, generation); _visible = true; UpdateUi(); MaybeTriggerPrewarm(); break;
                case 'B':
                    _selection = new Target(peer, generation);
                    if (_target is { } old && old.Peer == peer && old.Generation == generation) return;
                    _boundary = Math.Max(_sentence.Length, _delivered);
                    _targetRevision++;
                    Volatile.Write(ref _target, new Target(peer, generation));
                    MaybeTriggerPrewarm();
                    break;
                case 'T': Toggle(); break;
            }
        }

        private void MaybeTriggerPrewarm()
        {
            if (!_enablePrewarmRouting || !_visible || _audio != null || _prewarmed != null) return;
            var state = new PrewarmState(_lifetime.Token);
            _prewarmed = state;
            var previous = _released;
            _ = Task.Run(() => RunPrewarmAsync(state, previous));
        }

        private async Task RunPrewarmAsync(PrewarmState state, Task previous)
        {
            try
            {
                await previous.ConfigureAwait(false);
                state.Cts.Token.ThrowIfCancellationRequested();
                VoiceImeMaintenance.Check(_gate, _lifetime);
                var raw = await _load().WaitAsync(state.Cts.Token).ConfigureAwait(false);
                if (raw.VoiceIme is { EnablePrewarm: false })
                {
                    Post(() => { if (ReferenceEquals(_prewarmed, state)) _prewarmed = null; });
                    return;
                }
                var config = CreateRuntimeConfiguration(raw);
                state.Configuration = JsonSerializer.Serialize(config);
                state.Cts.Token.ThrowIfCancellationRequested();
                var session = _create(config);
                if (!state.TryAttach(session))
                {
                    await DisposeVoiceSessionAsync(session).ConfigureAwait(false);
                    return;
                }
                if (session is ChatVoiceInputSession production)
                    await production.PrewarmAsync(state.Cts.Token).ConfigureAwait(false);
                else if (session is VoiceImeTranslationSession translation)
                    await translation.PrewarmAsync(state.Cts.Token).ConfigureAwait(false);
            }
            catch
            {
                Post(() => { if (ReferenceEquals(_prewarmed, state)) DiscardPrewarm(); });
            }
        }

        private void DiscardPrewarm()
        {
            if (_prewarmed is not { } state) return;
            _prewarmed = null;
            var session = state.Take();
            if (session != null)
            {
                var previous = _released;
                _released = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    await DisposeVoiceSessionAsync(session).ConfigureAwait(false);
                });
            }
            state.Dispose();
        }

        private static async Task DisposeVoiceSessionAsync(IChatVoiceInputSession voice)
        {
            try { await voice.StopAsync().ConfigureAwait(false); } catch { }
            try { await voice.DisposeAsync().ConfigureAwait(false); } catch { }
            if (voice is ChatVoiceInputSession production)
            {
                try { await production.ResourcesReleased.ConfigureAwait(false); } catch { }
            }
            else if (voice is VoiceImeTranslationSession translation)
            {
                try { await translation.ResourcesReleased.ConfigureAwait(false); } catch { }
            }
        }

        private void OpenSettingsWindow()
        {
            DiscardPrewarm();
            _toolWindows?.Request(VoiceImeUiCommand.Settings);
        }

        private bool SafeForeground(Peer peer)
        {
            try { return !peer.Token.IsCancellationRequested && _foreground(peer.Pipe, peer.Thread); }
            catch { return false; }
        }

        private void Unbind(Peer peer, ulong? generation)
        {
            if (_target is not { } target || target.Peer != peer
                || (generation.HasValue && target.Generation != generation.Value)) return;
            Volatile.Write(ref _target, null);
            _targetRevision++;
            _boundary = Math.Max(_sentence.Length, _delivered);
        }

        private void Toggle()
        {
            if (_audio != null) { StopAudio(); return; }
            if (_target is not { } target || !SafeForeground(target.Peer)) return;
            VoiceImeMaintenance.Check(_gate, _lifetime);
            IChatVoiceInputSession? prewarmedVoice = null;
            string? prewarmedConfiguration = null;
            if (_prewarmed is { } state)
            {
                _prewarmed = null;
                prewarmedVoice = state.Take();
                prewarmedConfiguration = state.Configuration;
                state.Dispose();
            }
            var round = new AudioRound(Interlocked.Increment(ref _epoch), _lifetime.Token, prewarmedVoice);
            round.PrewarmedConfiguration = prewarmedConfiguration;
            round.UsageSession = _usage?.BeginSession();
            _audio = round;
            _sentence = ""; _boundary = _delivered = 0;
            _status = 'C'; _stopped = _error = false;
            Broadcast('C'); UpdateUi();
            var previous = _released;
            _released = Task.Run(() => RunAudioAsync(round, previous));
        }

        private void StopAudio(string reason = "manual_stop")
        {
            if (_audio is not { } round) return;
            _audio = null;
            Interlocked.Increment(ref _epoch); // 先隔离回调，再取消；连接中的第二个 T 不等 StartAsync
            round.NoTextTimer?.Dispose();
            round.NoTextTimer = null;
            round.UsageTimer?.Dispose();
            round.UsageTimer = null;
            // F8 本地结束 composition 保留末句，不等云端 final；翻译尚未输出的 partial 不补定稿。
            if (round.UsageSession is { } usageId)
                _usage?.EndSession(usageId, reason, finalizePending: reason == "manual_stop" && !round.Translating);
            round.Stop.Cancel();
            _sentence = ""; _boundary = _delivered = 0;
            _status = 'S'; _stopped = true;
            Broadcast('S'); UpdateUi();
        }

        private void OtherIme()
        {
            StopAudio("other_ime");
            DiscardPrewarm();
            _selection = null;
            Volatile.Write(ref _target, null);
            _visible = false;
            UpdateUi();
        }

        private async Task RunAudioAsync(AudioRound round, Task previous)
        {
            IChatVoiceInputSession? voice = round.PrewarmedVoice;
            FileStream? microphone = null;
            Task? starting = null;
            Action<string, bool> text = (value, final) => Post(() => Recognized(round, value, final));
            Action<string, bool> source = (value, final) => Post(() => SourceRecognized(round, value, final));
            Action<string> failed = message => Post(() => Failed(round, voice is VoiceImeTranslationSession ? message : null));
            var failure = false;
            try
            {
                // 即使此轮在排队时也被取消，仍保留整条释放链，不允许第三轮跳过第一轮。
                await previous.ConfigureAwait(false);
                round.Stop.Token.ThrowIfCancellationRequested();
                VoiceImeMaintenance.Check(_gate, _lifetime);
                if (_microphonePath != null)
                {
                    VoiceImeMaintenance.GuardPath(_microphonePath);
                    microphone = new FileStream(_microphonePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                var config = CreateRuntimeConfiguration(await _load().WaitAsync(round.Stop.Token).ConfigureAwait(false));
                if (voice != null && round.PrewarmedConfiguration != JsonSerializer.Serialize(config))
                {
                    await DisposeVoiceSessionAsync(voice).ConfigureAwait(false);
                    voice = null;
                }
                if (voice == null)
                {
                    round.Stop.Token.ThrowIfCancellationRequested();
                    VoiceImeMaintenance.Check(_gate, _lifetime);
                    voice = _create(config);
                }
                round.Translating = voice is VoiceImeTranslationSession;
                if (voice is VoiceImeTranslationSession translation) translation.SourceRecognized += source;
                voice.TextRecognized += text;
                voice.Failed += failed;
                starting = voice.StartAsync(round.Stop.Token);
                await starting.WaitAsync(round.Stop.Token).ConfigureAwait(false);
                Post(() =>
                {
                    if (!Current(round)) return;
                    _status = 'R'; Broadcast('R'); UpdateUi();
                    round.LastTextTimestamp = _timeProvider.GetTimestamp();
                    round.NoTextTimer = _timeProvider.CreateTimer(_ => Post(() => CheckNoTextTimeout(round)),
                        null, NoTextTimeout, Timeout.InfiniteTimeSpan);
                    if (round.UsageSession is { } usageId)
                    {
                        _usage!.RecordingStarted(usageId);
                        round.UsageTimer = _timeProvider.CreateTimer(_ => Post(() =>
                        {
                            if (!Current(round) || round.UsageTimer == null) return;
                            _usage.Checkpoint(usageId);
                            round.UsageTimer.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
                        }), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
                    }
                });
                await Task.Delay(Timeout.Infinite, round.Stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (round.Stop.IsCancellationRequested) { }
            catch { failure = true; Post(() => Failed(round)); }
            finally
            {
                round.Stop.Cancel();
                if (voice != null)
                {
                    voice.TextRecognized -= text;
                    if (voice is VoiceImeTranslationSession translated) translated.SourceRecognized -= source;
                    voice.Failed -= failed;
                    try { await voice.StopAsync().ConfigureAwait(false); } catch { failure = true; }
                    try { await voice.DisposeAsync().ConfigureAwait(false); } catch { failure = true; }
                    if (voice is ChatVoiceInputSession production)
                    {
                        try { await production.ResourcesReleased.ConfigureAwait(false); } catch { failure = true; }
                    }
                    else if (voice is VoiceImeTranslationSession translation)
                    {
                        try { await translation.ResourcesReleased.ConfigureAwait(false); } catch { failure = true; }
                    }
                }
                // 不让忽略取消的迟到 Start 在下一轮已经拿锁之后重新占麦；异常在这里被观察。
                if (starting != null) { try { await starting.ConfigureAwait(false); } catch { } }
                microphone?.Dispose();
                Post(() =>
                {
                    if (failure) Failed(round); // 旧 finally 不改新一轮状态
                    round.Stop.Dispose(); // actor 已处理 stop/failure 以后再释放 CTS
                });
            }
        }

        private bool Current(AudioRound round) => ReferenceEquals(_audio, round) && _epoch == round.Epoch;

        private void CheckNoTextTimeout(AudioRound round)
        {
            // Timer 回调只入 actor；已排队的旧回调不能停止新一轮，也不能覆盖刚到的文字。
            if (!Current(round) || round.NoTextTimer == null) return;
            var remaining = NoTextTimeout - _timeProvider.GetElapsedTime(round.LastTextTimestamp);
            if (remaining > TimeSpan.Zero)
                round.NoTextTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            else StopAudio("inactivity_timeout"); // 立即广播 S，并实际停止/释放识别器和麦克风。
        }

        private void Failed(AudioRound round, string? message = null)
        {
            if (!Current(round)) return;
            StopAudio("failed");
            _status = 'E'; _error = true;
            Broadcast('E', message ?? ErrorText); UpdateUi();
        }

        private void Recognized(AudioRound round, string text, bool final)
        {
            if (!Current(round)) return;
            if (round.Translating)
            {
                if (!final || !round.TranslationTargets.TryDequeue(out var captured)) return;
                if (captured.Target is { } destination && captured.Revision == _targetRevision
                    && destination == _target && SafeForeground(destination.Peer))
                    Send(destination.Peer, 'F', destination.Generation, text);
                return;
            }
            TrackSource(round, text, final);
            DeliverRecognized(text, final);
        }

        private void SourceRecognized(AudioRound round, string text, bool final)
        {
            if (!Current(round)) return;
            TrackSource(round, text, final);
            if (!string.IsNullOrWhiteSpace(text) && !round.SourceSentenceStarted)
            {
                round.SourceSentenceStarted = true;
                round.SourceTarget = _target;
                round.SourceRevision = _targetRevision;
            }
            if (!final) return;
            if (!string.IsNullOrWhiteSpace(text))
                round.TranslationTargets.Enqueue((round.SourceTarget, round.SourceRevision));
            round.SourceSentenceStarted = false;
            round.SourceTarget = null;
        }

        private void TrackSource(AudioRound round, string text, bool final)
        {
            if (round.UsageSession is { } usageId) _usage?.Recognized(usageId, text, final);
            if (!string.IsNullOrWhiteSpace(text) && round.NoTextTimer != null)
            {
                // partial/final 都算出字；空白结果、音量和焦点切换不延长计费会话。
                round.LastTextTimestamp = _timeProvider.GetTimestamp();
                round.NoTextTimer.Change(NoTextTimeout, Timeout.InfiniteTimeSpan);
            }
            }

            private void DeliverRecognized(string text, bool final)
            {
            _sentence = text;
            if (_target is { } target)
            {
                if (!SafeForeground(target.Peer)) Unbind(target.Peer, target.Generation);
                else
                {
                    var start = Math.Min(_boundary, text.Length);
                    // 新快照在冻结位置变成代理对时，只向后跳，绝不补回已归旧目标的 UTF-16 单元。
                    if (start > 0 && start < text.Length && char.IsHighSurrogate(text[start - 1]) && char.IsLowSurrogate(text[start])) start++;
                    Send(target.Peer, final ? 'F' : 'P', target.Generation, text[start..]);
                    _delivered = Math.Max(_delivered, text.Length);
                }
            }
            if (final) { _sentence = ""; _boundary = _delivered = 0; }
        }

        private void Broadcast(char kind, string text = "")
        { foreach (var peer in _peers) Send(peer, kind, 0, text); }

        private void Send(Peer peer, char kind, ulong generation, string text)
        {
            var epoch = Volatile.Read(ref _epoch);
            var payload = epoch.ToString(CultureInfo.InvariantCulture) + ":" + generation.ToString(CultureInfo.InvariantCulture) + ":" + text;
            try
            {
                if (StrictUtf8.GetByteCount(payload) + 1 > MaximumFrame
                    || !peer.Output.Writer.TryWrite(new Output(kind, payload, epoch, generation, _targetRevision))) peer.Close();
            }
            catch (EncoderFallbackException) { peer.Close(); }
        }

        private void UpdateUi() => _toolbar.Update(_visible, _status == 'C' ? 1 : _status == 'R' ? 2 : 0, _stopped, _error);

        private void UsageStatusChanged(VoiceImeUsageStatus _) => Post(UpdateHistory);
        private void UpdateHistory()
        {
            var status = _usage?.Status ?? new VoiceImeUsageStatus(false, false);
            if (status.Updating) return; // native 已自行进入 pending；只回传最终成功或失败。
            _toolbar.UpdateHistory(status.HistoryEnabled, status.Available);
        }

        private async Task WritePeerAsync(Peer peer)
        {
            try
            {
                await foreach (var frame in peer.Output.Reader.ReadAllAsync(peer.Token).ConfigureAwait(false))
                {
                    if (frame.Kind is 'P' or 'F')
                    {
                        var target = Volatile.Read(ref _target);
                        if (target == null || target.Peer != peer || target.Generation != frame.Generation
                            || Volatile.Read(ref _targetRevision) != frame.TargetRevision
                            || Volatile.Read(ref _epoch) != frame.Epoch || !SafeForeground(peer)) continue;
                    }
                    await WriteFrameAsync(peer.Pipe, frame.Kind, frame.Payload, peer.Token).ConfigureAwait(false);
                }
            }
            catch { peer.Close(); }
        }

        private static bool Decimal(string text, out ulong number)
        {
            number = 0;
            return text.Length is > 0 and <= 20 && text.All(c => c is >= '0' and <= '9')
                && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static async Task<(char Kind, string Text)> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaximumFrame) throw new InvalidDataException();
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return ((char)bytes[0], StrictUtf8.GetString(bytes.AsSpan(1)));
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _lifetime.Cancel();
            _commands.Writer.TryComplete();
            _toolbar.Close();
            if (_toolWindows != null) await _toolWindows.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAll(_actor, _accept, _monitor).ConfigureAwait(false);
            await Task.WhenAll(_connections).ConfigureAwait(false);
            await _released.ConfigureAwait(false);
            if (_usage != null) _usage.StatusChanged -= UsageStatusChanged;
            await _toolbar.DisposeAsync().ConfigureAwait(false);
            _slots.Dispose(); _lifetime.Dispose();
        }

        private sealed record Target(Peer Peer, ulong Generation);
        private sealed record Output(char Kind, string Payload, long Epoch, ulong Generation, long TargetRevision);
        private sealed class AudioRound(long epoch, CancellationToken token, IChatVoiceInputSession? prewarmedVoice = null)
        {
            internal readonly long Epoch = epoch;
            internal readonly IChatVoiceInputSession? PrewarmedVoice = prewarmedVoice;
            internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            internal ITimer? NoTextTimer;
            internal ITimer? UsageTimer;
            internal Guid? UsageSession;
            internal long LastTextTimestamp;
            internal string? PrewarmedConfiguration;
            internal bool Translating, SourceSentenceStarted;
            internal Target? SourceTarget;
            internal long SourceRevision;
            internal readonly Queue<(Target? Target, long Revision)> TranslationTargets = new();
        }
        private sealed class PrewarmState(CancellationToken token) : IDisposable
        {
            private readonly object _gate = new();
            private IChatVoiceInputSession? _session;
            private bool _disposed;
            internal readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            internal string? Configuration;
            internal bool TryAttach(IChatVoiceInputSession session)
            {
                lock (_gate)
                {
                    if (_disposed || Cts.IsCancellationRequested) return false;
                    _session = session;
                    return true;
                }
            }
            internal IChatVoiceInputSession? Take()
            {
                lock (_gate)
                {
                    var s = _session;
                    _session = null;
                    _disposed = true;
                    Cts.Cancel();
                    return s;
                }
            }
            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                    try { Cts.Cancel(); } catch { }
                    try { Cts.Dispose(); } catch { }
                }
            }
        }
        private sealed class Peer : IDisposable
        {
            internal readonly NamedPipeServerStream Pipe;
            internal readonly uint Thread;
            internal ulong Generation;
            private readonly object _closeGate = new();
            private readonly CancellationTokenSource _stop;
            private bool _closed, _disposed;
            internal CancellationToken Token { get; }
            internal readonly Channel<Output> Output = Channel.CreateBounded<Output>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            internal Peer(NamedPipeServerStream pipe, uint thread, CancellationToken token)
            { Pipe = pipe; Thread = thread; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); Token = _stop.Token; }
            internal void Close()
            {
                lock (_closeGate)
                {
                    if (_closed) return;
                    _closed = true;
                    _stop.Cancel();
                    Output.Writer.TryComplete();
                }
            }
            public void Dispose()
            {
                lock (_closeGate)
                {
                    if (_disposed) return;
                    Close();
                    _disposed = true;
                    _stop.Dispose();
                }
            }
        }
    }

    /// <summary>内存常驻配置缓存 + FileSystemWatcher 自动热重载，消除按下 F8 时的磁盘 I/O。</summary>
    private sealed class RuntimeConfigurationCache : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _path = new ConfigurationService().GetConfigFilePath();
        private FileSystemWatcher? _watcher;
        private AzureSpeechConfig? _cached;
        private DateTime _cachedWriteUtc;

        public RuntimeConfigurationCache()
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                var file = Path.GetFileName(_path);
                if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(file))
                {
                    Directory.CreateDirectory(directory);
                    _watcher = new FileSystemWatcher(directory, file)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    _watcher.Changed += (_, _) => Invalidate();
                    _watcher.Created += (_, _) => Invalidate();
                    _watcher.Renamed += (_, _) => Invalidate();
                }
            }
            catch { }
        }

        private void Invalidate()
        {
            lock (_gate) { _cached = null; }
        }

        public async Task<AzureSpeechConfig> GetAsync()
        {
            var writeUtc = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            lock (_gate)
            {
                if (_cached != null && _cachedWriteUtc == writeUtc)
                    return _cached;
            }
            var loaded = await LoadConfigurationAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _cached = loaded;
                _cachedWriteUtc = writeUtc;
            }
            return loaded;
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
        }
    }

    private sealed class SharedAuthentication(int sessionId, string host)
    {
        private readonly string _host = host;
        private readonly ConditionalWeakTable<NamedPipeServerStream, Tuple<int, uint>> _clients = new();
        internal bool Authenticate(NamedPipeServerStream pipe, uint thread)
        {
            try
            {
                if (thread == 0 || string.IsNullOrEmpty(_host)
                    || !GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)
                    || pid is 0 or > int.MaxValue) return false;
                using var process = Process.GetProcessById((int)pid);
                if (process.HasExited || process.SessionId != sessionId) return false;
                _clients.AddOrUpdate(pipe, Tuple.Create(process.Id, thread));
                return true;
            }
            catch { return false; }
        }

        internal bool Foreground(NamedPipeServerStream pipe, uint thread)
        {
            try
            {
                if (!_clients.TryGetValue(pipe, out var client) || client.Item2 != thread) return false;
                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                GetWindowThreadProcessId(fg, out var pid);
                return pid == (uint)client.Item1;
            }
            catch { return false; }
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}