using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using static Program;

internal static class PipeRegression
{
    internal static async Task VerifyDuplexAsync()
    {
        var voice = new FakeVoice { ImmediateText = true, HoldStop = true };
        await using var pipe = await SessionPipe.OpenAsync("duplex", voice);
        await pipe.ExpectAsync('R', "");
        await pipe.ExpectAsync('P', "你好，世界🌍");
        await pipe.ExpectAsync('F', "你好，世界🌍！");
        voice.Emit("第二句😊", false);
        voice.Emit("第二句😊完成。", true);
        await pipe.ExpectAsync('P', "第二句😊");
        await pipe.ExpectAsync('F', "第二句😊完成。");
        await pipe.StopAsync();
        await voice.StopEntered.Task.WaitAsync(Deadline);
        Check(voice.StartToken.IsCancellationRequested, "stop 没有取消识别源 token");
        // 模拟已经从事件源复制、即使退订仍可能到达的委托，不只调用当前事件。
        voice.EmitCapturedLate();
        voice.StopRelease.TrySetResult(true);
        await pipe.CompleteAsync(0, expectedFrames: 5);
        voice.AssertReleased();
        Note("PIPE R,P,F,P,F verified byte-for-byte; StartAsync synchronous callbacks gated by R; stop\\n cancelled, late callbacks discarded, disposed");
    }

    internal static async Task VerifyConnectingAsync()
    {
        var failures = new List<string>();
        await Case("stop-connecting", async () =>
        {
            var voice = new FakeVoice { Connecting = true };
            await using var pipe = await SessionPipe.OpenAsync("stop-connecting", voice);
            await voice.StartEntered.Task.WaitAsync(Deadline);
            Check(!voice.StartTask!.IsCompleted && voice.ActiveMicrophone == 1, "替身没有真正处于连接中");
            await pipe.StopAsync();
            await pipe.CompleteAsync(0, expectedFrames: 0);
            Check(voice.CancelObserved, "连接中停止未取消启动任务");
            voice.AssertReleased();
        });
        await Case("throw-connecting", async () =>
        {
            var voice = new FakeVoice { Connecting = true };
            await using var pipe = await SessionPipe.OpenAsync("throw-connecting", voice);
            await voice.StartEntered.Task.WaitAsync(Deadline);
            voice.Connection.TrySetException(new InvalidOperationException(ErrorCanary + " " + KeyCanary + " " + ConfigCanary));
            await pipe.ExpectAsync('E', "语音输入启动失败，请在主程序中检查输入法资源、模型、登录和麦克风设置。");
            await pipe.CompleteAsync(1, expectedFrames: 1);
            voice.AssertReleased();
        });
        await Case("callback-error-connecting", async () =>
        {
            var voice = new FakeVoice { Connecting = true };
            await using var pipe = await SessionPipe.OpenAsync("callback-error-connecting", voice);
            await voice.StartEntered.Task.WaitAsync(Deadline);
            voice.Fail(ErrorCanary + " " + KeyCanary + " " + ConfigCanary);
            await pipe.ExpectAsync('E', "语音识别中断，请在主程序中检查资源和麦克风后重试。");
            // E 已送达不等于会话结束。连接只听取消 token，Host 必须结束等待和释放它。
            // 失败退出为 1，区别于用户主动 stop 的 0；资源释放和迟到帧断言保持不变。
            await pipe.CompleteAsync(1, expectedFrames: 1);
            Check(voice.CancelObserved, "连接中 Failed 未取消识别源启动");
            voice.AssertReleased();
        });
        Check(failures.Count == 0, string.Join("; ", failures));

        async Task Case(string name, Func<Task> test)
        {
            try { await test(); Note("PASS connecting/" + name); }
            catch (CheckFailure ex) { failures.Add(name + ": " + ex.Message); Note("FAIL connecting/" + name + ": " + ex.Message); }
        }
    }

    private sealed class SessionPipe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<Frame> _received = Channel.CreateUnbounded<Frame>();
        private readonly List<Frame> _frames = new();
        private readonly FakeVoice _voice;
        private readonly string _name;
        private Task<int>? _host;
        private Task? _reader;
        private bool _ended;

        private SessionPipe(string name, FakeVoice voice)
        {
            _name = name;
            _voice = voice;
            var unique = $"TrueFluentPro.VoiceIme.Regression.{Environment.ProcessId}.{Guid.NewGuid():N}";
            _server = new NamedPipeServerStream(unique, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _client = new NamedPipeClientStream(".", unique, PipeDirection.InOut, PipeOptions.Asynchronous);
            Note("TEST_PIPE=" + unique);
        }

        public static async Task<SessionPipe> OpenAsync(string name, FakeVoice voice)
        {
            var pipe = new SessionPipe(name, voice);
            try
            {
                using var connect = new CancellationTokenSource(Deadline);
                await Task.WhenAll(pipe._server.WaitForConnectionAsync(connect.Token), pipe._client.ConnectAsync(connect.Token));
                pipe._reader = pipe.ReadAsync();
                var run = typeof(VoiceImeHost).GetMethod("RunSessionAsync", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new CheckFailure("缺少 RunSessionAsync");
                pipe._host = (Task<int>)run.Invoke(null, new object[] { pipe._server,
                    (Func<Task<AzureSpeechConfig>>)(() => Task.FromResult(Configuration())),
                    (Func<AzureSpeechConfig, IChatVoiceInputSession>)(_ => voice), pipe._stop.Token })!;
                return pipe;
            }
            catch { await pipe.DisposeAsync(); throw; }
        }

        public async Task ExpectAsync(char kind, string text)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            var frame = await _received.Reader.ReadAsync(deadline.Token);
            var body = Encoding.UTF8.GetBytes(kind + text);
            // 手工按协议构造预期 4-byte LE，与解析器的 ReadInt32LittleEndian 独立比对。
            var expected = new byte[4 + body.Length];
            for (var i = 0; i < 4; i++) expected[i] = (byte)(body.Length >> (i * 8));
            body.CopyTo(expected, 4);
            Check(frame.Kind == kind && frame.Text == text && Convert.FromHexString(frame.WireHex).SequenceEqual(expected),
                "帧顺序/4byteLE 字节长度/kind/UTF8 正文不符合协议：" + _name);
            Check(!frame.Text.Contains(ConfigCanary) && !frame.Text.Contains(KeyCanary) && !frame.Text.Contains(ErrorCanary),
                "错误帧泄漏配置/原始敏感异常");
        }

        public async Task StopAsync()
        {
            using var deadline = new CancellationTokenSource(Deadline);
            await _client.WriteAsync("stop\n"u8.ToArray(), deadline.Token);
            await _client.FlushAsync(deadline.Token);
        }

        public async Task CompleteAsync(int exit, int expectedFrames)
        {
            int actual;
            try { actual = await _host!.WaitAsync(Deadline); }
            catch (TimeoutException)
            {
                File.WriteAllText(Path.Combine(Artifacts, _name + "-before-cleanup.json"), JsonSerializer.Serialize(new
                {
                    status = "FAIL", timeoutSeconds = Deadline.TotalSeconds, externalCancellationIssued = false,
                    hostCompleted = _host!.IsCompleted, startCompleted = _voice.StartTask!.IsCompleted,
                    activeFakeMicrophone = _voice.ActiveMicrophone, stopCalls = _voice.StopCalls,
                    disposeCalls = _voice.DisposeCalls, sourceTokenCancelled = _voice.StartToken.IsCancellationRequested
                }, new JsonSerializerOptions { WriteIndented = true }));
                throw new CheckFailure($"{_name}: Host 未在 {Deadline.TotalSeconds}s 内结束；startPending={!_voice.StartTask!.IsCompleted}, activeFakeMicrophone={_voice.ActiveMicrophone}, stopCalls={_voice.StopCalls}, disposeCalls={_voice.DisposeCalls}");
            }
            Check(actual == exit, $"{_name}: session exit={actual}，预期 {exit}");
            _voice.EmitCapturedLate();
            // 先 Host 结束，再主动闭服务器取得 EOF；不是把无数据超时误作停止隔离成功。
            _server.Dispose();
            await _reader!.WaitAsync(Deadline);
            Check(_frames.Count == expectedFrames, $"{_name}: 停止/错误后又发送正文或多余帧，actual={_frames.Count}, expected={expectedFrames}");
            _ended = true;
            Note($"SESSION {_name}: exit={actual}, frames={_frames.Count}, startSettled={_voice.StartTask!.IsCompleted}, stop={_voice.StopCalls}, dispose={_voice.DisposeCalls}");
        }

        private async Task ReadAsync()
        {
            Exception? failure = null;
            try
            {
                while (true)
                {
                    var header = new byte[4];
                    var first = await _client.ReadAsync(header.AsMemory(0, 1), _stop.Token);
                    if (first == 0) break;
                    await _client.ReadExactlyAsync(header.AsMemory(1), _stop.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    Check(length is > 0 and <= 65536, "生产帧长度非法");
                    var body = new byte[length];
                    await _client.ReadExactlyAsync(body, _stop.Token);
                    var text = new UTF8Encoding(false, true).GetString(body.AsSpan(1));
                    var frame = new Frame((char)body[0], text, Convert.ToHexString(header.Concat(body).ToArray()));
                    _frames.Add(frame);
                    _received.Writer.TryWrite(frame);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { failure = ex; throw; }
            finally { _received.Writer.TryComplete(failure); }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_ended)
                {
                    // 仅测试收尾；不能把外部取消后的退出当作待测 Failed 自动收尾通过。
                    _stop.Cancel();
                    _voice.StopRelease.TrySetResult(true);
                    if (_host != null) await _host.WaitAsync(Deadline);
                }
                _server.Dispose();
                if (_reader != null) await _reader.WaitAsync(Deadline);
                if (_voice.StartTask != null)
                {
                    try { await _voice.StartTask.WaitAsync(Deadline); }
                    catch (OperationCanceledException) { }
                    catch (InvalidOperationException) when (_voice.StartTask.IsFaulted) { }
                    _voice.AssertReleased();
                }
            }
            finally
            {
                _stop.Cancel();
                _server.Dispose();
                _client.Dispose();
                File.WriteAllText(Path.Combine(Artifacts, _name + "-frames.json"), JsonSerializer.Serialize(_frames,
                    new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(Artifacts, _name + "-after-cleanup.json"), JsonSerializer.Serialize(new
                {
                    hostCompleted = _host?.IsCompleted, startCompleted = _voice.StartTask?.IsCompleted,
                    readerCompleted = _reader?.IsCompleted, activeFakeMicrophone = _voice.ActiveMicrophone,
                    stopCalls = _voice.StopCalls, disposeCalls = _voice.DisposeCalls, fallbackCleanup = !_ended
                }, new JsonSerializerOptions { WriteIndented = true }));
                Note($"CLEANUP {_name}: hostCompleted={_host?.IsCompleted}, startCompleted={_voice.StartTask?.IsCompleted}, readerCompleted={_reader?.IsCompleted}, activeFakeMicrophone={_voice.ActiveMicrophone}");
                _stop.Dispose();
            }
        }
    }

    private sealed record Frame(char Kind, string Text, string WireHex);

    private sealed class FakeVoice : IChatVoiceInputSession
    {
        public event Action<string, bool>? TextRecognized;
        public event Action<string>? Failed;
        public event Action<double>? AudioLevelChanged { add { } remove { } }
        public bool ImmediateText, Connecting, HoldStop, CancelObserved;
        public int ActiveMicrophone, StopCalls, DisposeCalls;
        public CancellationToken StartToken;
        public Task? StartTask;
        public readonly TaskCompletionSource<bool> StartEntered = Signal(), StopEntered = Signal(),
            Connection = Signal(), StopRelease = Signal();
        private Action<string, bool>? _capturedText;
        private Action<string>? _capturedFailure;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartToken = cancellationToken;
            _capturedText = TextRecognized;
            _capturedFailure = Failed;
            Interlocked.Exchange(ref ActiveMicrophone, 1);
            return StartTask = StartCoreAsync(cancellationToken);
        }

        private async Task StartCoreAsync(CancellationToken token)
        {
            StartEntered.TrySetResult(true);
            try
            {
                if (ImmediateText) { Emit("你好，世界🌍", false); Emit("你好，世界🌍！", true); }
                if (Connecting) await Connection.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { CancelObserved = true; throw; }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref StopCalls);
            Interlocked.Exchange(ref ActiveMicrophone, 0);
            StopEntered.TrySetResult(true);
            EmitCapturedLate();
            if (HoldStop) await StopRelease.Task.WaitAsync(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCalls);
            Interlocked.Exchange(ref ActiveMicrophone, 0);
            EmitCapturedLate();
            return ValueTask.CompletedTask;
        }
        public void Emit(string text, bool final) => TextRecognized?.Invoke(text, final);
        public void Fail(string text) => Failed?.Invoke(text);
        public void EmitCapturedLate()
        {
            _capturedText?.Invoke("停止后的正文绝不能发出🌙", false);
            _capturedText?.Invoke("停止后的定稿绝不能发出🌙", true);
            _capturedFailure?.Invoke(ErrorCanary);
        }
        public void AssertReleased()
        {
            Check(StartTask?.IsCompleted == true && ActiveMicrophone == 0 && StopCalls == 1 && DisposeCalls == 1,
                "存在未收尾启动任务/假麦克风会话，或停止/释放次数错误");
            Check(TextRecognized == null && Failed == null, "Host 没有解除事件订阅");
        }
    }
}