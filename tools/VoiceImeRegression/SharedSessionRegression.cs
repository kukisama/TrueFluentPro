using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using static Program;

internal static class SharedSessionRegression
{
    internal static async Task VerifyAsync()
    {
        VerifyPeerShutdown();
        await TransferAsync();
        await DeactivateAfterFocusLossAsync();
        await MaintenanceAsync(recording: false);
        await MaintenanceAsync(recording: true);
        await OwnerCloseAsync();
    }

    private static void VerifyPeerShutdown()
    {
        var type = typeof(VoiceImeHost).GetNestedType("MainSharedSession", BindingFlags.NonPublic)!
            .GetNestedType("Peer", BindingFlags.NonPublic)!;
        using var pipe = new NamedPipeServerStream("TrueFluentPro.VoiceIme.Fixture." + Guid.NewGuid().ToString("N"),
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var peer = (IDisposable)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [pipe, (uint)101, CancellationToken.None], null)!;
        var close = type.GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            close.Invoke(peer, null);
            peer.Dispose();
            try { close.Invoke(peer, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is ObjectDisposedException)
            { throw new CheckFailure("owner 收尾再次 Close 已 Dispose 的 peer 会抛 ObjectDisposedException"); }
            var token = (CancellationToken)type.GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(peer)!;
            Check(token.IsCancellationRequested, "peer 收尾未保留可查询的已取消 token");
        }
        finally { peer.Dispose(); }
        Note("SHARED peer shutdown cancel/dispose/close idempotent PASS");
    }

    private static async Task TransferAsync()
    {
        var first = new Voice(holdStop: true);
        var connecting = new Voice(connecting: true, holdStop: true);
        var next = new Voice();
        await using var f = new Fixture("transfer", first, connecting, next);
        await using var a = await f.ConnectAsync(101);
        await using var b = await f.ConnectAsync(202);
        await using var background = await f.ConnectAsync(303);
        await a.ExpectAsync('I', 0, 0, "");
        await b.ExpectAsync('I', 0, 0, "");
        await background.ExpectAsync('I', 0, 0, "");
        await using (var rejected = await f.ConnectAsync(999))
            await rejected.Ended.Task.WaitAsync(Deadline);

        f.Foreground = 101;
        await f.ShowAsync(a, 1);
        Check(f.Ui.Visible && f.Created == 0, "L 无 context 应显示但不能开麦");
        f.Ui.Event(1, 0);
        await f.ShowAsync(a, 2); // actor 顺序屏障，不用无数据超时证明没有启动
        Check(f.Created == 0, "无 B 时浮窗偷开录音");
        await f.BindAsync(a, 10);
        await a.SendAsync('T', 10);
        await a.ExpectAsync('C', 1, 0, "");
        await a.ExpectAsync('R', 1, 0, "");
        await b.ExpectAsync('C', 1, 0, "");
        await b.ExpectAsync('R', 1, 0, "");
        first.Emit("旧句🌍", false);
        await a.ExpectAsync('P', 1, 10, "旧句🌍");

        f.Foreground = 202;
        await f.BindAsync(b, 20);
        await a.SendAsync('U', 10);
        await a.DisposeAsync();
        await f.ShowAsync(b, 20);
        Check(first.Starts == 1 && first.Stops == 0 && !first.Token.IsCancellationRequested
            && f.Created == 1 && !f.Host.IsCompleted, "A 失焦/EOF 或 B 接管重新启动/结束了同一录音");
        first.Emit("旧句🌍接管:新增", false);
        await b.ExpectAsync('P', 1, 20, "接管:新增");
        await f.BindAsync(b, 20); // 重复 B 不得重新冻结
        first.Emit("旧句🌍接管:新增继续", false);
        await b.ExpectAsync('P', 1, 20, "接管:新增继续");

        await b.SendAsync('U', 20);
        await f.ShowAsync(b, 20);
        first.Emit("旧句🌍接管:新增继续未绑定丢弃", false);
        await f.BindAsync(b, 21);
        first.Emit("旧句🌍接管:新增继续未绑定丢弃未来", false);
        await b.ExpectAsync('P', 1, 21, "未来");
        first.Emit("旧句🌍接管:新增继续未绑定丢弃未来完", true);
        await b.ExpectAsync('F', 1, 21, "未来完");
        first.Emit("下一句😊", false);
        await b.ExpectAsync('P', 1, 21, "下一句😊");
        first.Emit("下一句😊完成", true);
        await b.ExpectAsync('F', 1, 21, "下一句😊完成");
        await f.ShowAsync(b, 22); // 新 generation 的 L 没有安全 B，不得沿用原目标
        first.Emit("甲乙", false);
        await f.BindAsync(b, 23);
        first.Emit("甲🌍新", true); // 冻结位置 2 落在代理对中间，只允许安全后缀
        await b.ExpectAsync('F', 1, 23, "新");
        Check(f.Ui.Updates.All(u => u.Visible) && f.Ui.Creates == 1 && f.Ui.Handle == 12345,
            "接管、readonly/unbound 或客户端退出隐藏/替换了唯一浮窗");
        Check(a.Frames.Count(frame => frame.Kind is 'P' or 'F') == 1,
            "B 接管后正文仍回到旧 A");

        await background.SendAsync('D', 30);
        await b.SendAsync('D', 22); // 同 peer 旧 generation
        await f.ShowAsync(b, 23);
        Check(f.Ui.Visible && first.Stops == 0, "后台 D/旧 generation D 伤到当前目标");
        await background.SendAsync('Q', 30); // 严格未知命令只关闭此 peer
        await background.Ended.Task.WaitAsync(Deadline);
        await f.ShowAsync(b, 23);
        Check(!first.Token.IsCancellationRequested && !f.Host.IsCompleted, "坏 peer 杀掉音频/owner");

        await b.SendAsync('T', 23);
        await b.ExpectAsync('S', 2, 0, "");
        await first.StopEntered.Task.WaitAsync(Deadline);
        Check(first.Token.IsCancellationRequested && first.Stops == 1 && first.Disposals == 0,
            "T 未立即取消或重复释放");
        first.Late();
        first.ReleaseStop();
        await first.Disposed.Task.WaitAsync(Deadline);
        first.AssertReleased();

        await b.SendAsync('T', 23);
        await b.ExpectAsync('C', 3, 0, "");
        await connecting.Started.Task.WaitAsync(Deadline);
        f.Ui.Event(1, 0); // StartAsync 尚 pending，UI 与 T 相同协调器
        await b.ExpectAsync('S', 4, 0, "");
        await connecting.StopEntered.Task.WaitAsync(Deadline);
        Check(connecting.Token.IsCancellationRequested && connecting.Stops == 1,
            "连接中 UI 第二次 toggle 被 reader await Start 阻塞");
        f.Ui.Event(1, 0); // 下一轮排队，旧释放期间不新开麦
        await b.ExpectAsync('C', 5, 0, "");
        await f.ShowAsync(b, 23);
        Check(f.Created == 2 && next.Starts == 0, "上一轮未释放便开始下一轮");
        connecting.Late();
        first.Late();
        connecting.ReleaseStop();
        await connecting.Disposed.Task.WaitAsync(Deadline);
        await b.ExpectAsync('R', 5, 0, "");
        connecting.AssertReleased();
        next.Emit("新 epoch", false);
        await b.ExpectAsync('P', 5, 23, "新 epoch");
        Check(!next.Token.IsCancellationRequested && next.Stops == 0, "旧 finally/回调取消新会话");

        await b.SendAsync('D', 23);
        await b.ExpectAsync('S', 6, 0, "");
        await next.Disposed.Task.WaitAsync(Deadline);
        await f.Ui.WaitAsync(u => !u.Visible);
        Check(!f.Host.IsCompleted && f.Ui.Closes == 0, "other IME 应停/隐藏但保留可复用 owner");
        next.AssertReleased();
        await f.ShowAsync(b, 24);
        f.Ui.Event(2, 7); // 未知不能当 other IME
        await f.ShowAsync(b, 24);
        Check(f.Ui.Visible, "未知 profile 状态隐藏浮窗");
        f.Ui.Event(2, 0);
        await f.Ui.WaitAsync(u => !u.Visible);
        f.Ui.Event(2, 1);
        await f.Ui.WaitAsync(u => u.Visible);
        File.WriteAllText(f.Gate, "synthetic maintenance");
        Check(await f.Host.WaitAsync(Deadline) == 0 && f.Ui.Closes == 1, "Idle owner 不响应维护/未关闭浮窗");
        Check(b.Frames.All(frame => frame.Kind is not ('P' or 'F') || frame.Text is
            "接管:新增" or "接管:新增继续" or "未来" or "未来完" or "下一句😊" or "下一句😊完成" or "新" or "新 epoch"),
            "无 target 回填、旧全文重复或迟到正文漏入新 epoch");
        Note("SHARED transfer: same recording/token, suffix-only handover, unbound discard, sentence reset, UTF16 boundary, bounded peer protocol, immediate connecting toggle, release chain and late-epoch isolation PASS");
    }

    private static async Task DeactivateAfterFocusLossAsync()
    {
        var voice = new Voice();
        await using var f = new Fixture("deactivate-after-focus-loss", voice);
        await using var a = await f.ConnectAsync(101);
        await using var b = await f.ConnectAsync(202);
        await a.ExpectAsync('I', 0, 0, "");
        await b.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(a, 10);
        await a.SendAsync('T', 10);
        await a.ExpectAsync('C', 1, 0, "");
        await a.ExpectAsync('R', 1, 0, "");
        await b.ExpectAsync('C', 1, 0, "");
        await b.ExpectAsync('R', 1, 0, "");

        f.Ui.Drain();
        f.Foreground = 202;
        await a.SendAsync('U', 10);
        // U 只解绑 target，selection 仍是 A；此处不能 Show B 掩盖失焦后的真实停用。
        // 普通失焦 + U + EOF 不停录的对照复用 TransferAsync。
        await a.SendAsync('D', 10);
        await a.ExpectAsync('S', 2, 0, "");
        await b.ExpectAsync('S', 2, 0, "");
        await voice.Disposed.Task.WaitAsync(Deadline);
        await f.Ui.WaitAsync(u => !u.Visible);
        var hiddenAfterDeactivate = !f.Ui.Visible;
        voice.AssertReleased();
        Check(hiddenAfterDeactivate && f.Created == 1 && !f.Host.IsCompleted && f.Ui.Closes == 0,
            "A 已失焦的真实 D 未停用/隐藏，或错误结束了 owner");

        await f.ShowAsync(b, 20); // 无 B/context 的 L 只显示，不重新采音。
        Check(f.Ui.Visible && voice.Starts == 1 && voice.Stops == 1 && voice.Disposals == 1 && f.Created == 1,
            "B 无 context 的 L 未显示或重新创建了采音会话");
        var hiddenUpdates = f.Ui.Updates.Count(u => !u.Visible);
        await a.SendAsync('D', 999); // selection 已是 B，旧 A 的更大 generation 也不能遮掉 B。
        await f.ShowAsync(b, 20); // 复用 L 的 actor 顺序屏障，不用无数据超时判断。
        Check(f.Ui.Visible && f.Ui.Updates.Count(u => !u.Visible) == hiddenUpdates
            && voice.Starts == 1 && voice.Stops == 1 && voice.Disposals == 1 && f.Created == 1
            && voice.Token.IsCancellationRequested && !f.Host.IsCompleted && f.Ui.Closes == 0,
            "旧 A D999 隐藏了新 B、重复停录/采音，或结束了 owner");
        Note($"SHARED deactivate-after-focus-loss: fakeStarts={voice.Starts}, fakeStops={voice.Stops}, fakeDisposals={voice.Disposals}, fakeCreated={f.Created}, tokenCancelled={voice.Token.IsCancellationRequested}, uiHiddenAfterD={hiddenAfterDeactivate}, uiVisibleAfterB={f.Ui.Visible}, ownerAlive={!f.Host.IsCompleted} PASS");
    }

    private static async Task MaintenanceAsync(bool recording)
    {
        var voice = new Voice();
        await using var f = new Fixture(recording ? "maintenance-recording" : "maintenance-idle", voice);
        await using var peer = await f.ConnectAsync(101);
        await peer.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(peer, 1);
        if (recording)
        {
            await peer.SendAsync('T', 1);
            await peer.ExpectAsync('C', 1, 0, "");
            await peer.ExpectAsync('R', 1, 0, "");
        }
        File.WriteAllText(f.Gate, "synthetic maintenance");
        Check(await f.Host.WaitAsync(Deadline) == 0 && f.Ui.Closes == 1 && !f.Ui.Visible,
            "维护未结束 Idle/recording owner");
        Check(recording ? voice.Token.IsCancellationRequested && voice.Stops == 1 && voice.Disposals == 1
            : f.Created == 0 && f.Loads == 0, "维护释放错误或 Idle 读取配置");
        if (recording) voice.AssertReleased();
        Note("SHARED maintenance " + (recording ? "recording" : "idle") + " PASS");
    }

    private static async Task OwnerCloseAsync()
    {
        var voice = new Voice();
        await using var f = new Fixture("owner-close", voice);
        await using var peer = await f.ConnectAsync(101);
        await peer.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(peer, 1);
        await peer.SendAsync('T', 1);
        await peer.ExpectAsync('C', 1, 0, "");
        await peer.ExpectAsync('R', 1, 0, "");
        f.Ui.Closed.TrySetResult(true); // native Run 结束，不是某个目标进程 EOF
        Check(await f.Host.WaitAsync(Deadline) == 0 && voice.Token.IsCancellationRequested
            && voice.Stops == 1 && voice.Disposals == 1 && f.Ui.Closes == 1, "owner UI 关闭未结束音频");
        voice.AssertReleased();
        Note("SHARED owner toolbar close PASS");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Voice[] _voices;
        internal readonly Ui Ui = new();
        internal readonly string Name, Gate;
        internal readonly Task<int> Host;
        internal uint Foreground;
        internal int Created, Loads;
        internal Fixture(string label, params Voice[] voices)
        {
            _voices = voices;
            Name = "TrueFluentPro.VoiceIme.Fixture." + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Artifacts, "shared", label);
            Directory.CreateDirectory(directory);
            Gate = Path.Combine(directory, "maintenance.lock");
            var run = typeof(VoiceImeHost).GetMethod("RunSharedFixtureAsync", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new CheckFailure("缺少 shared fixture 入口");
            Host = (Task<int>)run.Invoke(null, [Name, Gate,
                (Func<NamedPipeServerStream, uint, bool>)((_, thread) => thread is 101 or 202 or 303),
                (Func<NamedPipeServerStream, uint, bool>)((_, thread) => thread == Volatile.Read(ref Foreground)),
                (Func<Task<AzureSpeechConfig>>)(() => { Interlocked.Increment(ref Loads); return Task.FromResult(Configuration()); }),
                (Func<AzureSpeechConfig, IChatVoiceInputSession>)(_ => _voices[Interlocked.Increment(ref Created) - 1]),
                (Action<Action<int, int>>)(callback => { Ui.Callback = callback; Ui.Creates++; }),
                (Action<bool, int, bool, bool>)Ui.Update, (Action)Ui.Close, Ui.Closed.Task, _stop.Token])!;
            Note("SHARED_FIXTURE_PIPE=" + Name);
        }
        internal Task<Client> ConnectAsync(uint thread) => Client.OpenAsync(Name, thread);
        internal async Task ShowAsync(Client peer, ulong generation)
        {
            Ui.Drain();
            await peer.SendAsync('L', generation);
            await Ui.WaitAsync(u => u.Visible);
        }
        internal async Task BindAsync(Client peer, ulong generation)
        { await peer.SendAsync('B', generation); await ShowAsync(peer, generation); }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            foreach (var voice in _voices) voice.ReleaseStop();
            Ui.Closed.TrySetResult(true);
            await Host.WaitAsync(Deadline);
            _stop.Dispose();
        }
    }

    private sealed record UiUpdate(bool Visible, int State, bool Stopped, bool Error);
    private sealed class Ui
    {
        internal Action<int, int> Callback = null!;
        internal readonly TaskCompletionSource<bool> Closed = Signal();
        internal readonly ConcurrentQueue<UiUpdate> Updates = new();
        private readonly Channel<UiUpdate> _updates = Channel.CreateUnbounded<UiUpdate>();
        internal int Creates, Closes;
        internal int Handle => 12345;
        internal bool Visible;
        internal void Event(int kind, int value) => Callback(kind, value);
        internal void Update(bool visible, int state, bool stopped, bool error)
        {
            Visible = visible;
            var update = new UiUpdate(visible, state, stopped, error);
            Updates.Enqueue(update); _updates.Writer.TryWrite(update);
        }
        internal void Drain() { while (_updates.Reader.TryRead(out _)) { } }
        internal async Task WaitAsync(Func<UiUpdate, bool> predicate)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (!predicate(await _updates.Reader.ReadAsync(deadline.Token))) { }
        }
        internal void Close() { Interlocked.Increment(ref Closes); Closed.TrySetResult(true); }
    }

    // 复用已有 PipeRegression.FakeVoice；只包装调用计数，不复制识别器/采音实现。
    private sealed class Voice : IChatVoiceInputSession
    {
        private static readonly Type Fake = typeof(PipeRegression).GetNestedType("FakeVoice", BindingFlags.NonPublic)!;
        private readonly IChatVoiceInputSession _inner = (IChatVoiceInputSession)Activator.CreateInstance(Fake, true)!;
        internal int Starts, Stops, Disposals;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Started = Signal(), StopEntered = Signal(), Disposed = Signal();
        internal Voice(bool connecting = false, bool holdStop = false)
        { Fake.GetField("Connecting")!.SetValue(_inner, connecting); Fake.GetField("HoldStop")!.SetValue(_inner, holdStop); }
        public event Action<string, bool>? TextRecognized { add => _inner.TextRecognized += value; remove => _inner.TextRecognized -= value; }
        public event Action<string>? Failed { add => _inner.Failed += value; remove => _inner.Failed -= value; }
        public event Action<double>? AudioLevelChanged { add => _inner.AudioLevelChanged += value; remove => _inner.AudioLevelChanged -= value; }
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken; Interlocked.Increment(ref Starts);
            var task = _inner.StartAsync(cancellationToken);
            Started.TrySetResult(true); return task;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stops);
            var task = _inner.StopAsync(cancellationToken);
            StopEntered.TrySetResult(true); return task;
        }
        public async ValueTask DisposeAsync()
        { Interlocked.Increment(ref Disposals); await _inner.DisposeAsync(); Disposed.TrySetResult(true); }
        internal void Emit(string text, bool final) => Fake.GetMethod("Emit")!.Invoke(_inner, [text, final]);
        internal void Late() => Fake.GetMethod("EmitCapturedLate")!.Invoke(_inner, null);
        internal void ReleaseStop() => ((TaskCompletionSource<bool>)Fake.GetField("StopRelease")!.GetValue(_inner)!).TrySetResult(true);
        internal void AssertReleased()
        {
            Check(Starts == 1 && Stops == 1 && Disposals == 1 && Token.IsCancellationRequested, "shared 源实际调用/取消次数错误");
            Fake.GetMethod("AssertReleased")!.Invoke(_inner, null);
        }
    }

    private sealed record Frame(char Kind, long Epoch, ulong Generation, string Text);
    private sealed class Client : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly Channel<Frame> _received = Channel.CreateUnbounded<Frame>();
        private readonly Task _reader;
        private int _disposed;
        internal readonly ConcurrentQueue<Frame> Frames = new();
        internal readonly TaskCompletionSource<bool> Ended = Signal();
        private Client(NamedPipeClientStream pipe) { _pipe = pipe; _reader = ReadAsync(); }
        internal static async Task<Client> OpenAsync(string name, uint thread)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(Deadline);
            await pipe.ConnectAsync(deadline.Token);
            var client = new Client(pipe);
            await client.SendAsync('H', thread);
            return client;
        }
        internal async Task SendAsync(char kind, ulong generation)
        {
            var body = Encoding.UTF8.GetBytes(kind + generation.ToString(CultureInfo.InvariantCulture));
            var wire = new byte[body.Length + 4];
            for (var i = 0; i < 4; i++) wire[i] = (byte)(body.Length >> (8 * i));
            body.CopyTo(wire, 4);
            using var deadline = new CancellationTokenSource(Deadline);
            // 刻意拆封包，验证 read-exact 不依赖一次 read 返回完整 header/body。
            await _pipe.WriteAsync(wire.AsMemory(0, 2), deadline.Token);
            await _pipe.WriteAsync(wire.AsMemory(2), deadline.Token);
            await _pipe.FlushAsync(deadline.Token);
        }
        internal async Task ExpectAsync(char kind, long epoch, ulong generation, string text)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            var frame = await _received.Reader.ReadAsync(deadline.Token);
            Check(frame == new Frame(kind, epoch, generation, text),
                $"shared 帧预期 {kind}/{epoch}/{generation}，实际 {frame.Kind}/{frame.Epoch}/{frame.Generation}");
        }
        private async Task ReadAsync()
        {
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var header = new byte[4];
                    if (await _pipe.ReadAsync(header.AsMemory(0, 1)) == 0) break;
                    await _pipe.ReadExactlyAsync(header.AsMemory(1));
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    Check(length is >= 1 and <= 65536, "shared 封包长度不合法");
                    var body = new byte[length];
                    await _pipe.ReadExactlyAsync(body);
                    var payload = new UTF8Encoding(false, true).GetString(body.AsSpan(1));
                    var parts = payload.Split(':', 3);
                    Check(parts.Length == 3, "shared 缺 epoch/generation");
                    Check(!payload.Contains(KeyCanary) && !payload.Contains(ConfigCanary) && !payload.Contains(ErrorCanary), "shared 泄露原始异常/凭据");
                    var frame = new Frame((char)body[0], long.Parse(parts[0], CultureInfo.InvariantCulture),
                        ulong.Parse(parts[1], CultureInfo.InvariantCulture), parts[2]);
                    Frames.Enqueue(frame); _received.Writer.TryWrite(frame);
                }
            }
            catch (Exception ex) when (Volatile.Read(ref _disposed) != 0 && ex is IOException or ObjectDisposedException) { }
            catch (Exception ex) { failure = ex; }
            finally { _received.Writer.TryComplete(failure); Ended.TrySetResult(true); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _pipe.Dispose(); await _reader.WaitAsync(Deadline);
        }
    }
}