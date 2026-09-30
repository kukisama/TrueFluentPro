using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GptImageCli;

internal static class QueuePublishedTests
{
    // Opt-in, after publishing: await QueuePublishedTests.RunAsync(root, executable, png, Check);
    // Does not build, read user configuration, or use a remote service. Leaves artifacts under root.
    public static async Task RunAsync(string root, string executable, byte[] png, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: QueuePublishedTests requires Windows (worker lease / DPAPI / winsqlite3).");
            return;
        }

        root = Path.GetFullPath(root);
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("请传入已发布的 gpt-image.exe。", executable);
        if (png.Length == 0) throw new ArgumentException("请提供有效的 PNG 测试数据。", nameof(png));
        var suite = Path.Combine(root, "queue-published-" + Guid.NewGuid().ToString("N"));
        // Shared work budget, not 30 seconds per command; failure cleanup gets a separate bounded grace period.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await MainAsync(Path.Combine(suite, "main"), executable, png, check, deadline.Token);
        await MenuAsync(Path.Combine(suite, "menu"), executable, png, check, deadline.Token);
    }

    private static async Task MainAsync(string root, string executable, byte[] png,
        Action<bool, string> check, CancellationToken cancellation)
    {
        var parallel = Enumerable.Range(1, 4).Select(i => "parallel-" + i).ToArray();
        await using var test = new Scenario(root, executable, png, check, cancellation, parallel);
        await test.InitializeAsync();
        await test.ReplyAsync("pause");
        var jobs = new Dictionary<string, long>();
        foreach (var prompt in parallel.Concat(new[] { "retry-once", "always429" }))
            jobs.Add(prompt, await test.SubmitAsync(prompt));

        var paused = (await test.ReplyAsync("list")).Snapshot!;
        test.Check(paused.Paused && paused.WorkerRunning && paused.Jobs.Count == 6 &&
            paused.Jobs.All(j => j.State == JobState.Pending && j.Attempts == 0) && test.Server.Snapshot().Accepted == 0,
            "提交进程已退出，独立后台仍持有租约，暂停期间没有 HTTP");
        await test.ReplyAsync("resume");
        await test.Server.WaitAsync(s => s.Requests.Count(r => parallel.Contains(r.Prompt)) == 4, cancellation);

        var running = (await test.ReplyAsync("list")).Snapshot!;
        var policy = running.Queues.Single(q => q.Id == "image2");
        test.Check(running.WorkerRunning && !running.Paused && policy.MaxConcurrency == 4 && policy.Running == 4 &&
            policy.Pending == 2 && test.Server.Snapshot().Image2Active == 4 && test.Server.Snapshot().MaxImage2Active == 4,
            "配置并发 4：数据库四个 running 与服务端四个未完成请求同时成立");

        // Responses remain gated across BOTH starts: a second worker must neither recover nor resend these jobs.
        await Task.WhenAll(test.ReplyAsync("start"), test.ReplyAsync("start"));
        test.Check(test.Server.Snapshot().Accepted == 4 &&
            parallel.All(p => test.Store.Get(jobs[p]) is { State: JobState.Running, Attempts: 1 }),
            "重复 queue start 不重置在途任务，也不增加请求");

        jobs.Add("flare", await test.SubmitAsync("flare", "gpt-image-2.5-flare"));
        await test.Server.WaitAsync(s => s.Requests.Any(r => r.Prompt == "flare"), cancellation);
        test.Check(test.Store.Get(jobs["flare"])?.Queue == "flare" && test.Server.Snapshot().Image2Active == 4 &&
            test.Server.Snapshot().Requests.Single(r => r.Prompt == "flare").Model == "gpt-image-2.5-flare",
            "image2 四槽占满时，独立 flare 队列仍走实际 edit HTTP 路径");
        test.Server.Release();

        await test.Server.WaitAsync(s => s.Requests.Length >= 10, cancellation);
        await test.WaitForExitAsync();
        var wire = test.Server.Snapshot();
        test.Check(wire.Accepted == 10 && wire.Requests.Length == 10 && wire.MaxImage2Active == 4 &&
            parallel.All(p => wire.Requests.Count(r => r.Prompt == p) == 1) &&
            wire.Requests.Count(r => r.Prompt == "retry-once") == 2 &&
            wire.Requests.Count(r => r.Prompt == "always429") == 3 && wire.Requests.Count(r => r.Prompt == "flare") == 1,
            "主流程 HTTP 总数恰好 10：四普通 + 两次单重试 + 三次耗尽 + 一 flare，无重复付费模拟");
        test.Check(wire.Requests.All(r => r.ValidMultipart && r.Path == "/v1/images/edits" &&
            r.Model == (r.Prompt == "flare" ? "gpt-image-2.5-flare" : "gpt-image-2")),
            "每次实际 POST 均带正确模型、prompt、size、鉴权和完整 PNG multipart");

        foreach (var prompt in jobs.Keys.Where(p => p != "always429"))
        {
            var detail = await test.ReplyAsync("show", Id(jobs[prompt]));
            test.Check(detail.Job is { State: JobState.Succeeded, HttpStatus: 200 } &&
                detail.Job.Attempts == (prompt == "retry-once" ? 2 : 1), "成功任务持久化：" + prompt);
            test.CheckOutput(detail.Job!, prompt);
        }
        var retry = await test.ReplyAsync("show", Id(jobs["retry-once"]));
        var retryWire = wire.Requests.Where(r => r.Prompt == "retry-once").ToArray();
        test.Check(History(retry.Attempts!, 429, 200) && retryWire[0].Boundary != retryWire[1].Boundary &&
            retryWire.All(r => r.ValidMultipart), "429 后重新构建 multipart 和图片流，遵守一秒冷却后成功");
        var upstream = retry.Attempts![0].Result!.Value.GetProperty("api_error").GetProperty("upstream_message").GetString()!;
        test.Check(upstream.Contains("RPM Limit 2 Used 2 Retry after 1 second") &&
            upstream.Contains("[REDACTED]") && !upstream.Contains("upstream-private-secret"),
            "安装版 exe 保留上游限额与等待数值，过滤上游凭据，成功后仍保留该次详情");
        var exhausted = await test.ReplyAsync("show", Id(jobs["always429"]));
        test.Check(exhausted.Job is { State: JobState.Failed, Attempts: 3, HttpStatus: 429 } &&
            History(exhausted.Attempts!, 429, 429, 429) && !File.Exists(test.Output("always429")),
            "retryCount=2 表示总共三次；耗尽后失败，不写图片、不发第四次");
        var final = (await test.ReplyAsync("list")).Snapshot!;
        test.Check(!final.WorkerRunning && final.Jobs.All(j => !JobState.IsActive(j.State)),
            "后台完成后自动退出，list JSON 无活动任务");
        using var lease = test.Paths.TryLock("worker");
        test.Check(lease is not null, "后台完成后独占租约可重新取得");
    }

    private static async Task MenuAsync(string root, string executable, byte[] png,
        Action<bool, string> check, CancellationToken cancellation)
    {
        await using var test = new Scenario(root, executable, png, check, cancellation, ["menu-run"]);
        await test.InitializeAsync();
        var pauseText = await test.MenuAsync("2\nQ\n");
        test.Check(pauseText.Contains("全部队列已暂停发送") && test.Store.Snapshot(test.Paths, test.Paths.LoadSettings()).Paused,
            "实际菜单 2 全部暂停、Q 退出，暂停落库后才入队");
        var cancel = await test.SubmitAsync("menu-cancel");
        var clear1 = await test.SubmitAsync("menu-clear-1");
        var clear2 = await test.SubmitAsync("menu-clear-2");
        var decline = await test.MenuAsync($"5\n{Id(cancel)}\n4\nN\nQ\n");
        test.Check(decline.Contains("二次确认") && decline.Contains("未取消任何任务") &&
            test.Store.Get(cancel) is { State: JobState.Cancelled, Attempts: 0 } &&
            test.Store.Get(clear1) is { State: JobState.Pending, Attempts: 0 } &&
            test.Store.Get(clear2) is { State: JobState.Pending, Attempts: 0 } && test.Server.Snapshot().Accepted == 0,
            "菜单 5 按真实 ID 取消；菜单 4 拒绝二次确认保留 pending，未抢跑");

        // 8 -> group -> keep RPM -> concurrency 2 -> keep retry/delay -> 4 -> confirm -> Q.
        var confirmed = await test.MenuAsync("8\nimage2\n\n2\n\n\n4\nY\nQ\n");
        var settings = test.Paths.LoadSettings(); // Reads the file written by the EXE, not its menu text.
        var edited = settings.ForModel("gpt-image-2");
        test.Check(confirmed.Contains("队列配置已保存") && edited.MaxConcurrency == 2 &&
            edited.RequestsPerMinute == 10000 && edited.RetryCount == 2 && edited.RetryDelaySeconds == 1 &&
            settings.ForModel("gpt-image-2.5-flare").MaxConcurrency == 1,
            "菜单 8 确实写入配置文件：image2 并发改 2，其余字段及独立队列保持");
        test.Check(confirmed.Contains("已取消 2/2") && new[] { cancel, clear1, clear2 }.All(id =>
            test.Store.Get(id) is { State: JobState.Cancelled, Attempts: 0 } && test.Store.Attempts(id).Count == 0) &&
            test.Store.Snapshot(test.Paths, settings).Paused && test.Server.Snapshot().Accepted == 0,
            "菜单 4 确认只取消剩余任务，保留历史，全程没有 HTTP");

        var resume = await test.SubmitAsync("menu-run");
        var resumeText = await test.MenuAsync("3\nQ\n");
        await test.Server.WaitAsync(s => s.Requests.Length >= 1, cancellation);
        test.Check(resumeText.Contains("已解除全部暂停") && resumeText.Contains("已退出管理界面") &&
            !test.Store.Snapshot(test.Paths, test.Paths.LoadSettings()).Paused && test.Paths.WorkerRunning() &&
            test.Store.Get(resume) is { State: JobState.Running, Attempts: 1 },
            "菜单 3 恢复真实发送；Q 和管理进程退出不停止后台在途请求");
        test.Server.Release();
        await test.WaitForExitAsync();
        var result = await test.ReplyAsync("show", Id(resume));
        test.Check(result.Job is { State: JobState.Succeeded, Attempts: 1 } &&
            test.Server.Snapshot() is { Accepted: 1, Requests.Length: 1 } &&
            test.Server.Snapshot().Requests.Single() is
                { Prompt: "menu-run", Model: "gpt-image-2", Path: "/v1/images/edits", ValidMultipart: true },
            "菜单流程总共仅一次成功 HTTP，取消和清除的任务从未发送");
        test.CheckOutput(result.Job!, "menu-run");
    }

    private static bool History(List<QueueAttempt> attempts, params int[] statuses) =>
        attempts.Count == statuses.Length && attempts.Select(a => a.Number).SequenceEqual(Enumerable.Range(1, statuses.Length)) &&
        attempts.Select(a => a.HttpStatus).SequenceEqual(statuses.Select(s => (int?)s)) &&
        attempts.All(a => a.FinishedAt.HasValue) && attempts.Zip(attempts.Skip(1), (before, after) =>
            before.HttpStatus != 429 || after.StartedAt >= before.FinishedAt!.Value + 1000).All(valid => valid);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    // SQLite and the file lease have no async notification API. Only final persistence/lease release
    // uses a bounded 100ms observation loop; HTTP progress waits on the server's event below.
    private static async Task UntilAsync(Func<bool> complete, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (complete()) return;
            await Task.Delay(100, cancellation);
        }
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly string _root, _executable, _source, _key;
        private readonly byte[] _png;
        private readonly Action<bool, string> _check;
        private readonly CancellationToken _cancellation;
        private QueueStore? _store;
        public QueuePaths Paths { get; }
        public QueueStore Store => _store ??= new QueueStore(Paths);
        public LoopbackServer Server { get; }

        public Scenario(string root, string executable, byte[] png, Action<bool, string> check,
            CancellationToken cancellation, string[] heldPrompts)
        {
            _root = root; _executable = executable; _png = png; _check = check; _cancellation = cancellation;
            _key = "unique-fake-" + Guid.NewGuid().ToString("N");
            Paths = new QueuePaths(Path.Combine(root, "queue"));
            _source = Path.Combine(root, "参考图 source.png");
            File.WriteAllBytes(_source, png);
            Server = new LoopbackServer(png, _key, heldPrompts);
        }

        public void Check(bool condition, string name)
        {
            _check(condition, "queue published: " + name);
            // Keep the scenario safe even if the caller merely records failures instead of throwing.
            if (!condition) throw new InvalidOperationException("FAIL: queue published: " + name);
        }

        public async Task InitializeAsync()
        {
            var config = await ReplyAsync("config"); // The actual published EXE initializes settings and SQLite.
            Check(config.SettingsPath == Paths.SettingsFile && config.Settings is { Version: 1, Queues.Count: 3 } &&
                File.Exists(Paths.DatabaseFile), "queue config 初始化独立目录");
            var settings = Paths.LoadSettings();
            foreach (var policy in settings.Queues)
            {
                policy.RequestsPerMinute = 10000;
                policy.MaxConcurrency = policy.Id == "image2" ? 4 : 1;
                policy.RetryCount = 2;
                policy.RetryDelaySeconds = 1;
            }
            Paths.SaveSettings(settings);
            _ = Store;
        }

        public string Output(string prompt) => Path.Combine(_root, prompt + ".png");

        public async Task<long> SubmitAsync(string prompt, string model = "gpt-image-2")
        {
            var reply = Parse(await RunAsync([
                "submit", "--queue-dir", Paths.Root, "--no-config", "--endpoint", Server.Endpoint,
                "--api-key", _key, "--image-model", model, "--mode", "edit", "--image", _source,
                "--size", "1024x640", "--name", "发布测试 " + prompt, "--prompt", prompt,
                "--output", Output(prompt), "--timeout-minutes", "1", "--json"
            ]));
            Check(reply.JobId is > 0 && reply.WorkerRunning == true, "submit 已持久入队并退出：" + prompt);
            return reply.JobId!.Value;
        }

        public async Task<QueueReply> ReplyAsync(params string[] arguments) =>
            Parse(await RunAsync(["queue", .. arguments, "--queue-dir", Paths.Root, "--json"]));

        // QueueConsole is line-oriented text, NOT a QueueReply JSON document.
        public Task<string> MenuAsync(string script) => RunAsync(["queue", "--queue-dir", Paths.Root], script);

        private QueueReply Parse(string text)
        {
            var reply = JsonSerializer.Deserialize(text, QueueJsonContext.Default.QueueReply);
            Check(reply is { Ok: true }, "子进程返回有效成功 JSON");
            return reply!;
        }

        private async Task<string> RunAsync(string[] arguments, string script = "")
        {
            _cancellation.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(_executable)
            {
                WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var isolated = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "GPT_IMAGE_ENDPOINT",
                "OPENAI_BASE_URL", "AZURE_OPENAI_ENDPOINT", "GPT_IMAGE_API_KEY", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY",
                "GPT_IMAGE_TEXT_MODEL", "GPT_IMAGE_MODEL", "AZURE_OPENAI_API_VERSION" };
            foreach (var name in start.Environment.Keys.Where(k => isolated.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new InvalidOperationException("无法启动发布测试子进程。");
            // Drain both pipes immediately; never echo command arguments, stdout, stderr, or the fake credential.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync(script.AsMemory(), _cancellation);
                process.StandardInput.Close();
                await process.WaitForExitAsync(_cancellation);
                var text = await output.WaitAsync(_cancellation);
                var diagnostic = await error.WaitAsync(_cancellation);
                Check(!text.Contains(_key, StringComparison.Ordinal) && !diagnostic.Contains(_key, StringComparison.Ordinal),
                    "子进程输出不泄漏测试凭据");
                if (process.ExitCode != 0 || diagnostic.Length != 0)
                    throw new InvalidOperationException($"发布子进程失败（exit={process.ExitCode}）：{diagnostic.Trim()} {text.Trim()}");
                Check(process.ExitCode == 0 && diagnostic.Length == 0, "发布子进程成功退出（exit=" + process.ExitCode + "）");
                return text;
            }
            finally
            {
                // Only this exact child handle; never enumerate or kill detached/other-root workers.
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: false); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        public async Task WaitForExitAsync()
        {
            await UntilAsync(() => !Store.HasWork() && !Paths.WorkerRunning(), _cancellation);
            await Server.WaitAsync(s => s.Active == 0, _cancellation);
        }

        public void CheckOutput(QueueJob job, string prompt)
        {
            var expected = Output(prompt);
            Check(job.Result is { } result && result.GetProperty("ok").GetBoolean() &&
                result.GetProperty("files").EnumerateArray().Select(f => f.GetString()).SequenceEqual(new[] { expected }) &&
                File.Exists(expected) && File.ReadAllBytes(expected).SequenceEqual(_png), "结果 JSON 对应实际 PNG 文件：" + prompt);
        }

        public async ValueTask DisposeAsync()
        {
            Server.Release();
            try
            {
                if (File.Exists(Paths.DatabaseFile))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    Store.Pause(null, true); // No new dispatch; running requests are left to finish normally.
                    var quiet = 0;
                    await UntilAsync(() =>
                    {
                        // A finite in-flight 429 may become retry_wait during cleanup; cancel that too.
                        Store.Cancel();
                        quiet = !Paths.WorkerRunning() && Server.Snapshot().Active == 0 ? quiet + 1 : 0;
                        return quiet >= 3;
                    }, cleanup.Token);
                }
            }
            finally
            {
                // Keep accepting/responding until our worker has drained, then close and await every handler.
                // On a cleanup timeout, abort only OUR sockets, so a stuck HTTP call can unwind; no PID guessing.
                await Server.DisposeAsync();
            }
        }
    }

    private sealed record WireRequest(string Prompt, string Model, string Boundary, string Path, bool ValidMultipart);
    private sealed record ServerState(int Accepted, int Active, int Image2Active, int MaxImage2Active, WireRequest[] Requests);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly object _sync = new();
        private readonly List<Task> _handlers = [];
        private readonly List<WireRequest> _requests = [];
        private readonly HashSet<string> _held;
        private readonly byte[] _png, _success;
        private readonly string _key;
        private readonly TaskCompletionSource<bool> _release = Signal();
        private TaskCompletionSource<bool> _changed = Signal();
        private readonly Task _accept;
        private int _accepted, _active, _image2Active, _maxImage2Active;
        private string? _failure;
        public string Endpoint { get; }

        public LoopbackServer(byte[] png, string key, string[] heldPrompts)
        {
            _png = png; _key = key; _held = new HashSet<string>(heldPrompts, StringComparer.Ordinal);
            _success = JsonSerializer.SerializeToUtf8Bytes(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } });
            _listener.Start();
            Endpoint = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
            _accept = AcceptAsync();
        }

        private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Changed()
        {
            var previous = _changed;
            _changed = Signal();
            previous.TrySetResult(true);
        }
        public void Release() => _release.TrySetResult(true);
        public ServerState Snapshot()
        {
            lock (_sync) return new(_accepted, _active, _image2Active, _maxImage2Active, _requests.ToArray());
        }

        public async Task WaitAsync(Func<ServerState, bool> condition, CancellationToken cancellation)
        {
            while (true)
            {
                Task change;
                lock (_sync)
                {
                    if (_failure is not null) throw new IOException("回环测试服务失败（" + _failure + "）；未输出请求或凭据。");
                    if (condition(Snapshot())) return;
                    change = _changed.Task;
                }
                await change.WaitAsync(cancellation);
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    lock (_sync)
                    {
                        _accepted++; _active++;
                        _handlers.Add(HandleAsync(client));
                        Changed();
                    }
                }
            }
            catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            catch (Exception ex) { Fail(ex); }
        }

        private void Fail(Exception ex)
        {
            lock (_sync) { _failure ??= ex.GetType().Name; Changed(); }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var image2 = false;
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15)); // Gates/reads/writes are finite, even after test failure.
                var cancellation = timeout.Token;
                try
                {
                    using var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, cancellation);
                    int attempt;
                    lock (_sync)
                    {
                        _requests.Add(request);
                        attempt = _requests.Count(r => r.Prompt == request.Prompt);
                        image2 = request.Model == "gpt-image-2";
                        if (image2) _maxImage2Active = Math.Max(_maxImage2Active, ++_image2Active);
                        Changed();
                    }
                    if (_held.Contains(request.Prompt)) await _release.Task.WaitAsync(cancellation);
                    var limited = request.Prompt == "always429" || request.Prompt == "retry-once" && attempt == 1;
                    var body = limited ? JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        error = new
                        {
                            code = "rate_limit_exceeded",
                            message = "RPM Limit 2 Used 2 Retry after 1 second; api-key=" + _key +
                                "; upstream api-key=upstream-private-secret"
                        }
                    }) : _success;
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + (limited ? "429 Too Many Requests" : "200 OK") +
                        "\r\nContent-Type: application/json\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
                        "\r\nConnection: close\r\nx-request-id: offline-" + request.Prompt + "-" + attempt +
                        (limited ? "\r\nRetry-After: 1" : "") + "\r\n\r\n");
                    await stream.WriteAsync(headers, cancellation);
                    await stream.WriteAsync(body, cancellation);
                }
                catch (Exception ex) when (_stop.IsCancellationRequested && ex is OperationCanceledException or IOException or SocketException) { }
                catch (Exception ex) { Fail(ex); }
                finally
                {
                    lock (_sync) { _active--; if (image2) _image2Active--; Changed(); }
                }
            }
        }

        private async Task<WireRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellation)
        {
            using var headerBytes = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(one, cancellation);
                headerBytes.WriteByte(one[0]);
                if (headerBytes.Length > 32768) throw new IOException("请求头过长。");
                if (headerBytes.Length >= 4 && headerBytes.GetBuffer().AsSpan((int)headerBytes.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
            }
            var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var headers = Headers(lines.Skip(1));
            if (!headers.TryGetValue("Content-Length", out var rawLength) ||
                !int.TryParse(rawLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length is <= 0 or > 2_000_000)
                throw new IOException("测试 edit 请求必须有合理的 Content-Length。");
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, cancellation);
            var type = MediaTypeHeaderValue.Parse(headers["Content-Type"]);
            var boundary = type.Parameters.Single(p => p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)).Value!.Trim('"');
            // Latin1 is a byte-preserving view; do not decode binary PNG with UTF-8 or trim its bytes.
            var sections = Encoding.Latin1.GetString(body).Split("--" + boundary, StringSplitOptions.None);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var images = new List<byte[]>();
            var validImageHeaders = true;
            foreach (var section in sections.Skip(1).SkipLast(1))
            {
                var split = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (!section.StartsWith("\r\n", StringComparison.Ordinal) || !section.EndsWith("\r\n", StringComparison.Ordinal) || split < 2)
                    throw new IOException("multipart 分段不完整。");
                var partHeaders = Headers(section[2..split].Split("\r\n"));
                var disposition = ContentDispositionHeaderValue.Parse(partHeaders["Content-Disposition"]);
                var name = disposition.Name!.Trim('"');
                var value = Encoding.Latin1.GetBytes(section[(split + 4)..^2]);
                if (disposition.FileName is not null)
                {
                    validImageHeaders &= name == "image" && partHeaders.GetValueOrDefault("Content-Type") == "image/png";
                    images.Add(value);
                }
                else fields.Add(name, Encoding.UTF8.GetString(value));
            }
            var route = lines[0].Split(' ');
            var valid = route.Length == 3 && route[0] == "POST" && type.MediaType == "multipart/form-data" &&
                sections[0].Length == 0 && sections[^1] == "--\r\n" &&
                headers.GetValueOrDefault("Authorization") == "Bearer " + _key &&
                fields.GetValueOrDefault("size") == "1024x640" && fields.GetValueOrDefault("n") == "1" &&
                fields.GetValueOrDefault("output_format") == "png" && validImageHeaders &&
                images.Count == 1 && images[0].AsSpan().SequenceEqual(_png);
            return new(fields["prompt"], fields["model"], boundary, route[1], valid);
        }

        private static Dictionary<string, string> Headers(IEnumerable<string> lines) => lines.ToDictionary(
            line => line[..line.IndexOf(':')], line => line[(line.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);

        public async ValueTask DisposeAsync()
        {
            Release();
            _stop.Cancel();
            _listener.Stop();
            await _accept;
            Task[] handlers;
            lock (_sync) handlers = _handlers.ToArray();
            await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(3));
            _stop.Dispose();
        }
    }
}