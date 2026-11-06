using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using TrueFluentPro.Models;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using static Program;

internal static class SharedSessionRegression
{
    internal static async Task VerifyAsync()
    {
        VerifyPeerShutdown();
        await TransferAsync();
        await ManualPendingAsync();
        await TranslationAsync();
        await NoTextTimeoutAsync();
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

    private static async Task ManualPendingAsync()
    {
        var first = new Voice(holdStop: true);
        var next = new Voice();
        var clock = new ManualClock();
        using var db = new SqliteDbService(Path.Combine(Artifacts, "shared", "manual-pending", "usage.db"), 1);
        await using var usage = new VoiceImeUsageService(() => db, "shared-manual-pending-fixture", clock);
        Check(await usage.FlushAsync().WaitAsync(Deadline)
            && await usage.SetHistoryAsync(true).WaitAsync(Deadline), "手停末句夹具初始化/合成原文授权失败");
        await using var f = new Fixture("manual-pending", clock, usage, first, next);
        await using var peer = await f.ConnectAsync(101);
        await peer.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(peer, 10);
        await peer.SendAsync('T', 10);
        await peer.ExpectAsync('C', 1, 0, ""); await peer.ExpectAsync('R', 1, 0, "");
        await f.ShowAsync(peer, 10);
        foreach (var text in new[] { "旧快照", "修订快照", "最新快照🌍。" })
        {
            first.Emit(text, false);
            await peer.ExpectAsync('P', 1, 10, text); // 每个 partial 已经由真实 pipe 收到，再手停。
        }
        await peer.SendAsync('T', 10);
        await peer.ExpectAsync('S', 2, 0, "");
        // S 后直接 flush/query；不等 StopEntered，更不释放假 Stop，验证统计立即结束。
        var ended = await ExpectUsageAsync(("最新快照🌍。", 5));
        Check(first.Disposals == 0 && !first.Disposed.Task.IsCompleted,
            "手停末句断言没有保留尚未释放的识别源");
        await first.StopEntered.Task.WaitAsync(Deadline);
        // T 是 toggle，重复发送会启动下一轮；对刚查到的已结束 id 重复 EndSession 验幂等。
        usage.EndSession(ended[0].Id, "manual_stop", finalizePending: true);
        first.Late();
        await f.ShowAsync(peer, 10); // captured late 的 actor 屏障，不以无数据超时证明隔离。
        var afterRepeatedEnd = await ExpectUsageAsync(("最新快照🌍。", 5));
        Check(ended.SequenceEqual(afterRepeatedEnd),
            "重复结束或停止后 captured late 改写已结束会话/重复统计");
        first.ReleaseStop();
        await first.Disposed.Task.WaitAsync(Deadline);
        first.AssertReleased();

        await peer.SendAsync('T', 10); // 明确启动下一轮对照。
        await peer.ExpectAsync('C', 3, 0, ""); await peer.ExpectAsync('R', 3, 0, "");
        await f.ShowAsync(peer, 10);
        first.Late();
        usage.EndSession(ended[0].Id, "manual_stop", finalizePending: true); // 旧 id 不得结束新轮。
        next.Emit("下一轮", false);
        await peer.ExpectAsync('P', 3, 10, "下一轮");
        next.Emit("下一轮完成🌙。", true);
        await peer.ExpectAsync('F', 3, 10, "下一轮完成🌙。");
        await peer.SendAsync('T', 10);
        await peer.ExpectAsync('S', 4, 0, "");
        await next.Disposed.Task.WaitAsync(Deadline);
        next.AssertReleased();
        next.Late(); first.Late();
        await f.ShowAsync(peer, 10);
        f.Ui.Closed.TrySetResult(true);
        Check(await f.Host.WaitAsync(Deadline) == 0, "手停末句共享后台未正常收尾");
        await peer.Ended.Task.WaitAsync(Deadline);
        var rounds = await ExpectUsageAsync(("最新快照🌍。", 5), ("下一轮完成🌙。", 6));
        Check(rounds[0] == ended[0] && rounds[1].Id != ended[0].Id && f.Created == 2
            && peer.Frames.Where(frame => frame.Kind is 'P' or 'F').SequenceEqual(new[]
            {
                new Frame('P', 1, 10, "旧快照"), new Frame('P', 1, 10, "修订快照"),
                new Frame('P', 1, 10, "最新快照🌍。"), new Frame('P', 3, 10, "下一轮"),
                new Frame('F', 3, 10, "下一轮完成🌙。")
            }), "下一轮未独立统计、final后手停重计，或 captured late 漏入管道");
        Note("SHARED manual pending: authorized latest partial once before Stop release, duplicate EndSession/late isolation, next epoch final then manual stop counted once PASS");

        async Task<(Guid Id, long Revision)[]> ExpectUsageAsync(params (string Text, int Characters)[] expected)
        {
            Check(await usage.FlushAsync().WaitAsync(Deadline), "手停末句统计队列不可用");
            using var connection = db.CreateConnection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT s.session_id,s.revision,s.state,s.stop_reason,u.sequence_no,u.character_count,u.original_text "
                + "FROM voice_ime_sessions s LEFT JOIN voice_ime_utterances u ON u.session_id=s.session_id ORDER BY s.rowid,u.sequence_no;";
            using var rows = command.ExecuteReader();
            var sessions = new List<(Guid Id, long Revision)>();
            while (rows.Read())
            {
                var index = sessions.Count;
                Check(index < expected.Length && rows.GetString(2) == "ended" && rows.GetString(3) == "manual_stop"
                    && !rows.IsDBNull(4) && rows.GetInt64(4) == 1 && rows.GetInt64(5) == expected[index].Characters
                    && !rows.IsDBNull(6) && rows.GetString(6) == expected[index].Text,
                    "手停末句不是每轮一条最新快照、字符数/原文错误，或会话未立即结束");
                sessions.Add((Guid.Parse(rows.GetString(0)), rows.GetInt64(1)));
            }
            Check(sessions.Count == expected.Length, "手停末句遗漏或重复写入");
            return sessions.ToArray();
        }
    }

    private static async Task TranslationAsync()
    {
        var first = new Voice(holdStop: true);
        var next = new Voice();
        var requests = new[]
        {
            new TranslationRequest("第一句原文🌍"), new TranslationRequest("第二句原文"),
            new TranslationRequest("甲框句首切框后定稿"), new TranslationRequest("乙框后续原文"),
            new TranslationRequest("乙框等待期间往返切换"), new TranslationRequest("往返后的正常句"),
            new TranslationRequest("停止时在途原文"), new TranslationRequest("停止前已排队原文"),
            new TranslationRequest("重启后的原文")
        };
        var calls = 0;
        var translatedEvents = new ConcurrentQueue<(string Text, bool Final)>();
        using var db = new SqliteDbService(Path.Combine(Artifacts, "shared", "translation", "usage.db"), 1);
        var clock = new ManualClock();
        await using var usage = new VoiceImeUsageService(() => db, "shared-translation-fixture", clock);
        Check(await usage.FlushAsync().WaitAsync(Deadline)
            && await usage.SetHistoryAsync(true).WaitAsync(Deadline), "翻译统计夹具初始化/合成原文授权失败");
        await using var f = new Fixture("translation", clock, usage, Wrap, first, next);
        await using var a = await f.ConnectAsync(101);
        await using var b = await f.ConnectAsync(202);
        await a.ExpectAsync('I', 0, 0, ""); await b.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(a, 10);
        await a.SendAsync('T', 10);
        await a.ExpectAsync('C', 1, 0, ""); await a.ExpectAsync('R', 1, 0, "");
        await b.ExpectAsync('C', 1, 0, ""); await b.ExpectAsync('R', 1, 0, "");
        await f.Ui.WaitAsync(u => u.Visible && u.State == 2);

        first.Emit("第一句", false); first.Emit("第一句原文🌍", false);
        await f.ShowAsync(a, 10);
        await ExpectUsageAsync(0);
        Check(Volatile.Read(ref calls) == 0 && translatedEvents.IsEmpty
            && !a.Frames.Any(frame => frame.Kind is 'P' or 'F'), "原文partial启动翻译、被统计或泄漏到TSF管道");
        first.Emit(requests[0].Source, true);
        await requests[0].WaitStartedAsync();
        first.Emit("第二句", false); first.Emit(requests[1].Source, true);
        await f.ShowAsync(a, 10);
        await ExpectUsageAsync(2); // 译文还没返回，也必须按原文final各记一次。
        Check(Volatile.Read(ref calls) == 1 && translatedEvents.IsEmpty, "第二句越过尚未完成的第一句");
        requests[1].Result.SetResult("Second sentence, already ready.");
        requests[0].Result.SetResult("First🌍.");
        await a.ExpectAsync('F', 1, 10, "First🌍.");
        await a.ExpectAsync('F', 1, 10, "Second sentence, already ready.");
        await requests[1].WaitStartedAsync();
        await f.ShowAsync(a, 10); await ExpectUsageAsync(2);

        first.Emit("甲框句首", false);
        await f.ShowAsync(a, 10); // 句首已在A捕获，不能等final才选目标。
        f.Foreground = 202;
        await f.BindAsync(b, 20);
        first.Emit(requests[2].Source, true);
        await requests[2].WaitStartedAsync();
        first.Emit("乙框后续", false); first.Emit(requests[3].Source, true);
        requests[2].Result.SetResult("Must not enter B after A sentence start.");
        await requests[3].WaitStartedAsync(); // 失效句仍须消费FIFO占位，不能挪给后句。
        requests[3].Result.SetResult("B完整译文🌍。");
        await b.ExpectAsync('F', 1, 20, "B完整译文🌍。");

        first.Emit(requests[4].Source, true);
        await requests[4].WaitStartedAsync();
        f.Foreground = 101; await f.BindAsync(a, 10);
        f.Foreground = 202; await f.BindAsync(b, 20); // 同peer、同generation，revision已改变。
        first.Emit(requests[5].Source, true);
        requests[4].Result.SetResult("Must not reappear after B to A to B.");
        await requests[5].WaitStartedAsync();
        requests[5].Result.SetResult("往返后整句。");
        await b.ExpectAsync('F', 1, 20, "往返后整句。");
        Check(f.Created == 1 && first.Starts == 1 && first.Stops == 0,
            "翻译等待/目标切换意外重建或停止了同一识别源");

        first.Emit(requests[6].Source, true);
        await requests[6].WaitStartedAsync();
        first.Emit(requests[7].Source, true);
        await f.ShowAsync(b, 20); await ExpectUsageAsync(8);
        await b.SendAsync('T', 20);
        await b.ExpectAsync('S', 2, 0, ""); await a.ExpectAsync('S', 2, 0, "");
        await first.StopEntered.Task.WaitAsync(Deadline);
        Check(requests[6].Token.IsCancellationRequested && first.Token.IsCancellationRequested
            && first.Stops == 1 && first.Disposals == 0 && !requests[7].Started.Task.IsCompleted,
            "stop未取消在途翻译、错误启动排队句，或未等待识别源释放");
        await b.SendAsync('T', 20);
        await b.ExpectAsync('C', 3, 0, ""); await a.ExpectAsync('C', 3, 0, "");
        await f.ShowAsync(b, 20);
        Check(f.Created == 1 && next.Starts == 0, "旧翻译会话释放前重开识别源");
        first.Late(); first.ReleaseStop();
        await first.Disposed.Task.WaitAsync(Deadline);
        await b.ExpectAsync('R', 3, 0, ""); await a.ExpectAsync('R', 3, 0, "");
        // delegate故意不合作：新轮已ready时旧翻译任务仍pending，才返回迟到译文。
        Check(!requests[6].Result.Task.IsCompleted, "停止回归未真正保留在途翻译任务");
        requests[6].Result.SetResult("Late translation must not enter restarted target.");
        first.Late();
        next.Emit("重启后的", false); next.Emit(requests[8].Source, true);
        await requests[8].WaitStartedAsync();
        requests[8].Result.SetResult("Fresh epoch, complete sentence.");
        await b.ExpectAsync('F', 3, 20, "Fresh epoch, complete sentence.");
        next.Emit("翻译新轮未定稿末句", false);
        await f.ShowAsync(b, 20); // 确保包装器原文 partial 已进入统计 pending，再手停。
        await b.SendAsync('T', 20);
        await b.ExpectAsync('S', 4, 0, ""); await a.ExpectAsync('S', 4, 0, "");
        await next.Disposed.Task.WaitAsync(Deadline);
        first.AssertReleased(); next.AssertReleased();
        // 第9句已经成功完成，其每请求deadline已Dispose，不要求随后stop取消已结束请求的token。
        // 正在进行的第7句取消在上方检查；新轮音频取消由next.AssertReleased验证。
        Check(Volatile.Read(ref calls) == 8
            && !requests[7].Started.Task.IsCompleted, "新轮未取消或停止后翻译了旧排队句");
        f.Ui.Closed.TrySetResult(true);
        Check(await f.Host.WaitAsync(Deadline) == 0, "翻译共享后台未正常收尾");
        await Task.WhenAll(a.Ended.Task, b.Ended.Task).WaitAsync(Deadline);
        await ExpectUsageAsync(9);
        Check(a.Frames.Where(frame => frame.Kind is 'P' or 'F').SequenceEqual(new[]
        {
            new Frame('F', 1, 10, "First🌍."), new Frame('F', 1, 10, "Second sentence, already ready.")
        }) && b.Frames.Where(frame => frame.Kind is 'P' or 'F').SequenceEqual(new[]
        {
            new Frame('F', 1, 20, "B完整译文🌍。"), new Frame('F', 1, 20, "往返后整句。"),
            new Frame('F', 3, 20, "Fresh epoch, complete sentence.")
        }), "pipe EOF后发现原文partial/final、旧目标、迟到或被截短的译文");
        Check(translatedEvents.ToArray().SequenceEqual(new[]
        {
            ("First🌍.", true), ("Second sentence, already ready.", true),
            ("Must not enter B after A sentence start.", true), ("B完整译文🌍。", true),
            ("Must not reappear after B to A to B.", true), ("往返后整句。", true),
            ("Fresh epoch, complete sentence.", true)
        }), "生产翻译器未按句序完成、失效结果未实际返回或stop迟到仍发出事件");
        Note("SHARED translation: real duplex pipes + production adapter; partial silent, FIFO complete finals, sentence-start target, revision roundtrip discard, next sentence, stop cancellation/late result/restart, manual pending excluded, source-only SQLite counts PASS");

        IChatVoiceInputSession Wrap(Voice voice)
        {
            var constructor = typeof(VoiceImeTranslationSession).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(IChatVoiceInputSession), typeof(Func<string, CancellationToken, Task<string>>)], null)
                ?? throw new CheckFailure("缺少可控翻译internal构造函数");
            var session = (VoiceImeTranslationSession)constructor.Invoke([voice,
                (Func<string, CancellationToken, Task<string>>)((text, token) =>
                {
                    var index = Interlocked.Increment(ref calls) - 1;
                    // 停止前的排队句不得执行；新轮仅允许自己的首句。
                    var request = requests[ReferenceEquals(voice, first) ? index : 8];
                    request.ActualSource = text; request.Token = token; request.Started.TrySetResult(true);
                    return request.Result.Task;
                })]);
            session.TextRecognized += (text, final) => translatedEvents.Enqueue((text, final));
            return session;
        }

        async Task ExpectUsageAsync(int count)
        {
            Check(await usage.FlushAsync().WaitAsync(Deadline), "翻译原文统计队列不可用");
            using var connection = db.CreateConnection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT u.original_text,u.character_count,u.sequence_no FROM voice_ime_utterances u "
                + "JOIN voice_ime_sessions s ON s.session_id=u.session_id ORDER BY s.rowid,u.sequence_no;";
            using var rows = command.ExecuteReader();
            var index = 0;
            while (rows.Read())
            {
                Check(index < count && !rows.IsDBNull(0) && rows.GetString(0) == requests[index].Source
                    && rows.GetInt64(1) == VoiceImeUsageService.CountCharacters(requests[index].Source)
                    && rows.GetInt64(2) == (index < 8 ? index + 1 : 1),
                    "翻译用量重复统计、存入译文/迟到原文，或原文句序/字符数错误");
                index++;
            }
            Check(index == count, "翻译等待或丢弃目标时遗漏原文统计");
        }
    }

    private sealed class TranslationRequest(string source)
    {
        internal readonly string Source = source;
        internal string? ActualSource;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<bool> Started = Signal();
        internal readonly TaskCompletionSource<string> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitStartedAsync()
        {
            await Started.Task.WaitAsync(Deadline);
            Check(ActualSource == Source && Token.CanBeCanceled, "翻译请求的原文FIFO或取消token错误");
        }
    }

    private static async Task NoTextTimeoutAsync()
    {
        var clock = new ManualClock();
        var first = new Voice();
        var next = new Voice();
        await using var f = new Fixture("no-text-timeout", clock, first, next);
        await using var peer = await f.ConnectAsync(101);
        await peer.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101;
        await f.BindAsync(peer, 1);
        await peer.SendAsync('T', 1);
        await peer.ExpectAsync('C', 1, 0, "");
        await peer.ExpectAsync('R', 1, 0, "");
        await f.ShowAsync(peer, 1); // 等 R 的 actor 完整建立 timer。
        var oldTimer = clock.Latest;
        Check(oldTimer.Due == TimeSpan.FromSeconds(60), "无字超时不是生产60秒");

        clock.Advance(TimeSpan.FromSeconds(59));
        await f.ShowAsync(peer, 1);
        Check(first.Stops == 0, "59秒提前停录");
        first.Emit("临时文字", false);
        await peer.ExpectAsync('P', 1, 1, "临时文字");
        clock.Advance(TimeSpan.FromSeconds(59));
        oldTimer.FireQueued(); // 模拟重置前已经排队的计时回调。
        await f.ShowAsync(peer, 1);
        Check(first.Stops == 0, "partial未续期或旧timer误停");
        first.Emit("完成文字", true);
        await peer.ExpectAsync('F', 1, 1, "完成文字");
        clock.Advance(TimeSpan.FromSeconds(59));
        first.Emit(" \t", false);
        await peer.ExpectAsync('P', 1, 1, " \t");
        await f.ShowAsync(peer, 1);
        Check(first.Stops == 0, "final未续期或59秒误停");
        clock.Advance(TimeSpan.FromSeconds(1));
        await peer.ExpectAsync('S', 2, 0, "");
        await first.Disposed.Task.WaitAsync(Deadline);
        await f.Ui.WaitAsync(u => u.Visible && u.State == 0 && u.Stopped && !u.Error);
        first.AssertReleased();
        Check(oldTimer.IsDisposed && !f.Host.IsCompleted, "自动停录未释放timer或结束了owner");
        first.Late();

        await peer.SendAsync('T', 1);
        await peer.ExpectAsync('C', 3, 0, "");
        await peer.ExpectAsync('R', 3, 0, "");
        await f.ShowAsync(peer, 1);
        oldTimer.FireQueued(); // 已 Dispose 的回调仍可能排队。
        await f.ShowAsync(peer, 1);
        Check(next.Stops == 0, "旧轮次无字timer停止新录音");
        clock.Advance(TimeSpan.FromSeconds(59));
        await f.ShowAsync(peer, 1);
        Check(next.Stops == 0, "重启后的无字录音提前停止");
        clock.Advance(TimeSpan.FromSeconds(1));
        await peer.ExpectAsync('S', 4, 0, "");
        await next.Disposed.Task.WaitAsync(Deadline);
        next.AssertReleased();
        Check(peer.Frames.Count(frame => frame.Kind == 'S') == 2
            && peer.Frames.Where(frame => frame.Kind is 'P' or 'F').Count() == 3,
            "自动停录重复广播或迟到文字泄漏");
        Note("SHARED no-text timeout: production 60s, 59s control, partial/final reset, whitespace ignored, actual stop/dispose once, queued callback and restart isolation PASS");
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

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Voice[] _voices;
        internal readonly Ui Ui = new();
        internal readonly string Name, Gate;
        internal readonly Task<int> Host;
        internal uint Foreground;
        internal int Created, Loads;
        internal Fixture(string label, params Voice[] voices) : this(label, TimeProvider.System, voices) { }
        internal Fixture(string label, TimeProvider clock, params Voice[] voices) : this(label, clock, null, voices) { }
        internal Fixture(string label, TimeProvider clock, VoiceImeUsageService? usage, params Voice[] voices)
            : this(label, clock, usage, voice => voice, voices) { }
        internal Fixture(string label, TimeProvider clock, VoiceImeUsageService? usage,
            Func<Voice, IChatVoiceInputSession> wrap, params Voice[] voices)
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
                (Func<AzureSpeechConfig, IChatVoiceInputSession>)(_ => wrap(_voices[Interlocked.Increment(ref Created) - 1])),
                (Action<Action<int, int>>)(callback => { Ui.Callback = callback; Ui.Creates++; }),
                (Action<bool, int, bool, bool>)Ui.Update, (Action)Ui.Close, Ui.Closed.Task, _stop.Token, clock,
                usage, (Action<bool, bool>)Ui.UpdateHistory])!;
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

    // 仅 fixture 的可控单调时钟；生产仍使用 System TimeProvider 的真实60秒。
    internal sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        private readonly List<ManualTimer> _timers = new();
        internal readonly DateTimeOffset Origin = new(2026, 11, 6, 15, 59, 50, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Origin.AddTicks(GetTimestamp());
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Fixture+8", TimeSpan.FromHours(8), "Fixture+8", "Fixture+8");
        internal ManualTimer Latest = null!;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Latest = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(Latest);
            return Latest;
        }
        internal void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref _timestamp, duration.Ticks);
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }
        internal sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private long _deadline;
            internal TimeSpan Due;
            internal bool IsDisposed;
            internal ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
            { _clock = clock; _callback = callback; _state = state; Change(due, period); }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == Timeout.InfiniteTimeSpan, "无字检查应为单次timer");
                if (IsDisposed) return false;
                Due = dueTime; _deadline = _clock.GetTimestamp() + dueTime.Ticks;
                return true;
            }
            internal void FireIfDue()
            {
                if (!IsDisposed && _clock.GetTimestamp() >= _deadline)
                { _deadline = long.MaxValue; FireQueued(); }
            }
            internal void FireQueued() => _callback(_state);
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed record UiUpdate(bool Visible, int State, bool Stopped, bool Error);
    internal sealed class Ui
    {
        internal Action<int, int> Callback = null!;
        internal readonly TaskCompletionSource<bool> Closed = Signal();
        internal readonly ConcurrentQueue<UiUpdate> Updates = new();
        private readonly Channel<UiUpdate> _updates = Channel.CreateUnbounded<UiUpdate>();
        private readonly Channel<VoiceImeUsageStatus> _history = Channel.CreateUnbounded<VoiceImeUsageStatus>();
        internal VoiceImeUsageStatus History;
        internal void UpdateHistory(bool enabled, bool available)
        { History = new(enabled, available); _history.Writer.TryWrite(History); }
        internal async Task WaitHistoryAsync(bool enabled, bool available)
        {
            using var deadline = new CancellationTokenSource(Deadline);
            while (await _history.Reader.ReadAsync(deadline.Token) != new VoiceImeUsageStatus(enabled, available)) { }
        }
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
    internal sealed class Voice : IChatVoiceInputSession
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
        internal void Fail() => Fake.GetMethod("Fail")!.Invoke(_inner, [ErrorCanary]);
        internal void Late() => Fake.GetMethod("EmitCapturedLate")!.Invoke(_inner, null);
        internal void ReleaseStop() => ((TaskCompletionSource<bool>)Fake.GetField("StopRelease")!.GetValue(_inner)!).TrySetResult(true);
        internal void AssertReleased()
        {
            Check(Starts == 1 && Stops == 1 && Disposals == 1 && Token.IsCancellationRequested, "shared 源实际调用/取消次数错误");
            Fake.GetMethod("AssertReleased")!.Invoke(_inner, null);
        }
    }

    internal sealed record Frame(char Kind, long Epoch, ulong Generation, string Text);
    internal sealed class Client : IAsyncDisposable
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