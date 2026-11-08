using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using static Program;
using static SharedSessionRegression;

internal static class UsageRegression
{
    internal static async Task VerifyAsync()
    {
        await PendingStopAsync();
        await PrivacyAndHostAsync();
        await LifecycleAsync();
        await StorageAsync();
        await FailureAsync();
    }

    private static SqliteDbService Database(string label) => new(Path.Combine(Artifacts, "usage", label + ".db"), 1);
    private static async Task Flush(VoiceImeUsageService usage) => Check(await usage.FlushAsync().WaitAsync(Deadline), "统计队列不可用");
    private static object? Scalar(ISqliteDbService db, string sql)
    {
        using var c = db.CreateConnection(); using var cmd = c.CreateCommand();
        cmd.CommandText = sql; return cmd.ExecuteScalar();
    }
    private static long Number(ISqliteDbService db, string sql) => Convert.ToInt64(Scalar(db, sql));

    private static async Task PendingStopAsync()
    {
        using var db = Database("pending-stop"); var clock = new ManualClock();
        await using var usage = new VoiceImeUsageService(() => db, "pending-stop-fixture", clock);
        await Flush(usage);
        var defaultOff = usage.BeginSession();
        usage.Recognized(defaultOff, "默认末句。", false);
        Pending(4, null);
        usage.EndSession(defaultOff, "manual_stop", finalizePending: true);
        await Row(defaultOff, 1, 4, null);
        Check(await usage.SetHistoryAsync(true).WaitAsync(Deadline), "末句fixture授权失败");

        var manual = usage.BeginSession(); usage.RecordingStarted(manual);
        usage.Recognized(manual, "手停", false);
        usage.Recognized(manual, "手停末句 e\u0301👨‍👩‍👧‍👦。", false);
        usage.Recognized(manual, "手停末句 e\u0301👨‍👩‍👧‍👦。", false);
        Pending(6, "手停末句 e\u0301👨‍👩‍👧‍👦。");
        await Row(manual, 0, 0, null); // 重复 partial 不落盘，也不累计修订字数。
        clock.Advance(TimeSpan.FromSeconds(3));
        usage.EndSession(manual, "manual_stop", finalizePending: true);
        usage.EndSession(manual, "manual_stop", finalizePending: true);
        usage.Recognized(manual, "旧回调不能补记", true);
        await Row(manual, 1, 6, "手停末句 e\u0301👨‍👩‍👧‍👦。");
        Check(Number(db, $"SELECT recording_ms FROM voice_ime_sessions WHERE session_id='{manual:D}';") == 3000
            && (string?)Scalar(db, $"SELECT state FROM voice_ime_sessions WHERE session_id='{manual:D}';") == "ended",
            "收尾末句改变了会话时长/结束状态");

        var finalized = usage.BeginSession();
        usage.Recognized(finalized, "未定稿", false);
        usage.Recognized(finalized, "同文定稿。", true);
        Pending(0, null, hasPending: false);
        usage.Recognized(finalized, "同文定稿。", false);
        usage.Recognized(finalized, "同文定稿。", true); // 同文不同句仍独立计数。
        usage.EndSession(finalized, "manual_stop", finalizePending: true);
        await Row(finalized, 2, 8, "同文定稿。");

        var privateSentence = usage.BeginSession();
        usage.Recognized(privateSentence, "句首已授权", false);
        var off = usage.SetHistoryAsync(false);
        Pending(5, null); // pending 尚未提交时必须已清除内存原文。
        Check(await off.WaitAsync(Deadline), "末句关闭原文失败");
        Pending(5, null);
        var on = usage.SetHistoryAsync(true);
        Pending(5, null);
        Check(await on.WaitAsync(Deadline), "末句重新授权失败");
        usage.Recognized(privateSentence, "句中重新开启不补正文。", false);
        Pending(10, null);
        usage.EndSession(privateSentence, "manual_stop", finalizePending: true);
        await Row(privateSentence, 1, 10, null);

        Check(await usage.SetHistoryAsync(false).WaitAsync(Deadline), "末句关闭前置失败");
        var startedOff = usage.BeginSession();
        usage.Recognized(startedOff, "开启前旧句", false);
        Check(await usage.SetHistoryAsync(true).WaitAsync(Deadline), "末句开启前置失败");
        usage.Recognized(startedOff, "开启前旧句修订。", false);
        Pending(7, null);
        usage.EndSession(startedOff, "manual_stop", finalizePending: true);
        await Row(startedOff, 1, 7, null);

        var translation = usage.BeginSession();
        usage.Recognized(translation, "翻译原文partial尚未输入", false);
        usage.EndSession(translation, "manual_stop"); // 翻译调用者必须保持默认 false。
        usage.Recognized(translation, "翻译结束后迟到final", true);
        await Row(translation, 0, 0, null);
        var empty = usage.BeginSession();
        usage.Recognized(manual, "旧会话partial", false);
        usage.EndSession(empty, "manual_stop", finalizePending: true);
        await Row(empty, 0, 0, null);
        Note("USAGE explicit pending stop: latest count/text once; default NULL; final reset and identical next sentence; privacy pending/off/on no refill; translation default discard; ended/old callback isolation PASS");

        void Pending(int characters, string? original, bool hasPending = true)
        {
            var round = Field(typeof(VoiceImeUsageService), "_round").GetValue(usage)!;
            Check((int)Field(round.GetType(), "PendingCharacters").GetValue(round)! == characters
                && (bool)Field(round.GetType(), "HasPending").GetValue(round)! == hasPending
                && (string?)Field(round.GetType(), "PendingOriginal").GetValue(round) == original,
                "末句快照/原文立即清除/定稿清理错误");
        }
        async Task Row(Guid id, long count, long characters, string? original)
        {
            await Flush(usage);
            var where = $"FROM voice_ime_utterances WHERE session_id='{id:D}'";
            Check(Number(db, $"SELECT COUNT(*) {where};") == count
                && Number(db, $"SELECT COALESCE(SUM(character_count),0) {where};") == characters
                && Number(db, $"SELECT COUNT(original_text) {where};") == (original == null ? 0 : count)
                && (original == null || (string?)Scalar(db, $"SELECT original_text {where} ORDER BY sequence_no DESC LIMIT 1;") == original),
                "末句计数/正文/防重错误");
        }
    }

    private static async Task PrivacyAndHostAsync()
    {
        using var db = Database("privacy");
        var clock = new ManualClock();
        await using (var usage = new VoiceImeUsageService(() => db, "fixture", clock))
        await using (var f = new Fixture("usage-privacy", clock, usage, new Voice()))
        await using (var peer = await f.ConnectAsync(101))
        {
            await peer.ExpectAsync('I', 0, 0, ""); f.Foreground = 101;
            await f.BindAsync(peer, 1);
            await f.Ui.WaitHistoryAsync(false, true);
            Check(usage.Status == new VoiceImeUsageStatus(false, true), "新安装默认原文不是off");
            await peer.SendAsync('T', 1);
            await peer.ExpectAsync('C', 1, 0, ""); await peer.ExpectAsync('R', 1, 0, "");
            await f.ShowAsync(peer, 1);
            // 从 fixture 取实际创建的假源，不启动任何真实音频。
            var voice = (Voice[])typeof(Fixture).GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f)!;
            var v = voice[0];
            await Emit("合成默认", false, 'P'); await Emit("合成默认修订", false, 'P');
            await Flush(usage);
            Check(Number(db, "SELECT COUNT(*) FROM voice_ime_utterances;") == 0, "partial 被统计");
            await Emit("合成默认。", true, 'F'); await Emit("合成默认。", true, 'F');
            await Flush(usage);
            Check(Number(db, "SELECT COUNT(*) FROM voice_ime_utterances;") == 2
                && Number(db, "SELECT SUM(character_count) FROM voice_ime_utterances;") == 8
                && Number(db, "SELECT COUNT(original_text) FROM voice_ime_utterances;") == 0, "同文不同句未独立计数或默认存了正文");

            await Emit("开启前旧句", false, 'P');
            await Toggle(true);
            await Emit("开启前旧句定稿", true, 'F'); // 开启前已有partial，必须NULL。
            await Emit("授权后的完整句。", false, 'P'); await Emit("授权后的完整句。", true, 'F');
            await Emit("中途关闭又开启", false, 'P');
            await Toggle(false); await Toggle(true);
            await Emit("中途关闭又开启定稿", true, 'F');
            await Emit("纯final授权句。", true, 'F');
            await Toggle(false);
            await Emit("关闭期间旧句", false, 'P'); await Emit("关闭期间定稿", true, 'F');
            await Emit("关闭期间未完句", false, 'P'); await Toggle(true);
            await Emit("关闭期间未完句又开启", true, 'F');
            await Flush(usage);
            Check(Number(db, "SELECT COUNT(original_text) FROM voice_ime_utterances;") == 2
                && Number(db, "SELECT COUNT(*) FROM voice_ime_utterances WHERE sequence_no IN (4,6) AND original_text IS NOT NULL;") == 2,
                "中途切换补存旧句、丢失已授权句或关闭删除历史");
            var unicode = " 中，A e\u0301𠮷 👨‍👩‍👧‍👦 👍🏽 🇨🇳 1️⃣ \t。！？—‘’（）；\u200B\u0301";
            Check(VoiceImeUsageService.CountCharacters(unicode) == 8, "Unicode字素/代理对/emoji/标点空白计数错误");
            Check(VoiceImeUsageService.CountCharacters(" \t\r\n，。！？—‘’（）_\u200B\u0301") == 0, "标点或孤立组合符被统计");
            clock.Advance(TimeSpan.FromSeconds(20)); await f.ShowAsync(peer, 1);
            await Emit(unicode, true, 'F');
            await peer.SendAsync('T', 1); await peer.ExpectAsync('S', 2, 0, "");
            await v.Disposed.Task.WaitAsync(Deadline); v.AssertReleased();
            await Flush(usage);
            Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions;") == 20000
                && (string?)Scalar(db, "SELECT stop_reason FROM voice_ime_sessions;") == "manual_stop", "手动停止时长/原因错误");
            Check((string?)Scalar(db, "SELECT created_local_date FROM voice_ime_sessions;") == "2026-11-06"
                && (string?)Scalar(db, "SELECT local_date FROM voice_ime_utterances WHERE sequence_no=9;") == "2026-11-07"
                && Number(db, "SELECT utc_offset_minutes FROM voice_ime_utterances WHERE sequence_no=9;") == 480,
                "UTC/本地跨日及偏移记录错误");
            Note("USAGE partial ignored; identical finals are separate sentences; default NULL; host kind4 persisted on/off; partial privacy generations; Unicode graphemes; UTC/local midnight; manual duration PASS");

            async Task Emit(string text, bool final, char kind)
            { v.Emit(text, final); await peer.ExpectAsync(kind, 1, 1, text); await f.ShowAsync(peer, 1); }
            async Task Toggle(bool enabled)
            {
                f.Ui.Event(4, enabled ? 1 : 0);
                await f.ShowAsync(peer, 1); // 确认请求已被actor消费。
                await Flush(usage);
                await f.Ui.WaitHistoryAsync(enabled, true);
                Check(usage.Status == new VoiceImeUsageStatus(enabled, true), "设置成功后仍在pending或原文状态错误");
            }
        }
        await using (var restarted = new VoiceImeUsageService(() => db, "fixture", clock))
        {
            await Flush(restarted);
            Check(restarted.Status.HistoryEnabled, "授权设置没有跨宿主重启保留");
            var id = restarted.BeginSession(); restarted.Recognized(id, "重启后纯final", true);
            restarted.EndSession(id, "owner_exit"); await Flush(restarted);
            Check(Number(db, "SELECT COUNT(original_text) FROM voice_ime_utterances;") == 4, "重启后授权原文未保存");
            Check(await restarted.SetHistoryAsync(false), "重启后关闭未持久化");
        }
        await using (var restartedOff = new VoiceImeUsageService(() => db, "fixture", clock))
        { await Flush(restartedOff); Check(!restartedOff.Status.HistoryEnabled, "off设置未跨重启保留"); }
        Note("USAGE persisted on and off across service/host restarts, closing keeps historical rows PASS");
    }

    private static async Task LifecycleAsync()
    {
        using var db = Database("lifecycle"); var clock = new ManualClock();
        await using var usage = new VoiceImeUsageService(() => db, "fixture", clock);
        var first = new Voice(); var next = new Voice(); var failed = new Voice(connecting: true); var last = new Voice();
        await using var f = new Fixture("usage-lifecycle", clock, usage, first, next, failed, last);
        await using var p = await f.ConnectAsync(101);
        await p.ExpectAsync('I', 0, 0, ""); f.Foreground = 101; await f.BindAsync(p, 1);
        await Start(1); await f.ShowAsync(p, 1); var oldCheckpoint = clock.Latest;
        clock.Advance(TimeSpan.FromSeconds(15)); await f.ShowAsync(p, 1); await Flush(usage);
        Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions;") == 15000
            && Scalar(db, "SELECT ended_utc FROM voice_ime_sessions;") is DBNull, "15秒检查点未保存或伪造终止");
        clock.Advance(TimeSpan.FromSeconds(44)); await f.ShowAsync(p, 1);
        Check(first.Stops == 0, "15秒统计timer改变了60秒无字规则");
        clock.Advance(TimeSpan.FromSeconds(1)); await p.ExpectAsync('S', 2, 0, "");
        await first.Disposed.Task.WaitAsync(Deadline); await Flush(usage);
        Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions;") == 60000
            && (string?)Scalar(db, "SELECT stop_reason FROM voice_ime_sessions;") == "inactivity_timeout", "超时原因/时长错误");
        await Start(3); await f.ShowAsync(p, 1);
        oldCheckpoint.FireQueued(); first.Late(); await f.ShowAsync(p, 1); await Flush(usage);
        Check(next.Stops == 0 && Number(db, "SELECT COUNT(*) FROM voice_ime_utterances;") == 0, "旧轮次写入/停了新录音");
        clock.Advance(TimeSpan.FromSeconds(5)); await f.ShowAsync(p, 1);
        await p.SendAsync('D', 1); await p.ExpectAsync('S', 4, 0, ""); await next.Disposed.Task.WaitAsync(Deadline);
        await Flush(usage);
        Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions WHERE stop_reason='other_ime';") == 5000, "other IME原因/时长错误");
        await f.BindAsync(p, 2); await p.SendAsync('T', 2); await p.ExpectAsync('C', 5, 0, "");
        await failed.Started.Task.WaitAsync(Deadline); failed.Fail();
        await p.ExpectAsync('S', 6, 0, ""); await p.ExpectAsync('E', 6, 0, "语音输入已中断，请在主程序中检查语音资源和麦克风后重试。");
        await failed.Disposed.Task.WaitAsync(Deadline); await Flush(usage);
        Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions WHERE stop_reason='failed';") == 0
            && Scalar(db, "SELECT recording_started_utc FROM voice_ime_sessions WHERE stop_reason='failed';") is DBNull,
            "连接失败伪造了已录音时长");
        await p.SendAsync('T', 2); await p.ExpectAsync('C', 7, 0, ""); await p.ExpectAsync('R', 7, 0, "");
        await f.ShowAsync(p, 2); clock.Advance(TimeSpan.FromSeconds(3));
        f.Ui.Closed.TrySetResult(true); Check(await f.Host.WaitAsync(Deadline) == 0, "owner关闭失败"); await Flush(usage);
        Check(Number(db, "SELECT recording_ms FROM voice_ime_sessions WHERE stop_reason='owner_exit';") == 3000, "owner_exit时长错误");
        first.AssertReleased(); next.AssertReleased(); failed.AssertReleased(); last.AssertReleased();
        Note("USAGE 15s checkpoints; unchanged 60s timeout; old round isolation; other_ime/failed/owner_exit; connecting release PASS");
        async Task Start(long epoch)
        { await p.SendAsync('T', 1); await p.ExpectAsync('C', epoch, 0, ""); await p.ExpectAsync('R', epoch, 0, ""); }
    }

    private static async Task StorageAsync()
    {
        using var db = Database("storage");
        // 两个独立实例同时初始化共享库，随后并发写不丢失彼此的meta键。
        using var other = new SqliteDbService(db.DatabasePath, 1);
        await Task.WhenAll(Task.Run(db.EnsureCreated), Task.Run(other.EnsureCreated));
        await Task.WhenAll(Task.Run(() => db.SetMeta("fixture_ui", "retained")), Task.Run(() => other.SetMeta("fixture_host", "retained")));
        Check(db.GetMeta("fixture_ui") == "retained" && db.GetMeta("fixture_host") == "retained"
            && Number(db, "SELECT COUNT(*) FROM _schema_version;") == 1, "共享初始化/版本/meta写入不幂等");
        using (var c = db.CreateConnection())
        {
            using var command = c.CreateCommand();
            Check(c.DefaultTimeout == 1 && command.CommandTimeout == 1, "IME有界超时没有传到命令");
            command.CommandText = "PRAGMA journal_mode;";
            Check((string?)command.ExecuteScalar() == "wal", "未启用WAL");
        }
        // 用真实生产Store重放相同record，验证重试幂等，不另造一套INSERT来测自己。
        var type = typeof(VoiceImeUsageService);
        var storeType = type.GetNestedType("Store", BindingFlags.NonPublic)!;
        var store = Activator.CreateInstance(storeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [db], null)!;
        object Record(string name, params object?[] values) => Activator.CreateInstance(type.GetNestedType(name, BindingFlags.NonPublic)!, values)!;
        void Write(string name, object record) => storeType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, [record]);
        var stamp = Record("UsageTime", "2026-11-06T15:59:50.0000000+00:00", "2026-11-06", 480);
        var id = Guid.NewGuid();
        var session = Record("Session", id, "fixture", stamp, stamp, stamp, 15000L, "recording", null, 2L);
        Write("WriteSession", session); Write("WriteSession", session);
        var sentence = Record("Utterance", id, 1L, stamp, 2, null);
        Write("WriteUtterance", sentence); Write("WriteUtterance", sentence);
        Check(Number(db, "SELECT COUNT(*) FROM voice_ime_utterances;") == 1, "重复DB写入重复计数");
        // 保留未结束行模拟异常退出；重启不以当前时间伪造结束或延长时长。
        var unrelated = Record("Session", Guid.NewGuid(), "other-desktop", stamp, stamp, stamp, 6000L, "recording", null, 1L);
        Write("WriteSession", unrelated);
        await using (var recovered = new VoiceImeUsageService(() => db, "fixture"))
        {
            await Flush(recovered);
            Check((string?)Scalar(db, "SELECT stop_reason FROM voice_ime_sessions WHERE owner_scope='fixture';") == "interrupted"
                && Number(db, "SELECT recording_ms FROM voice_ime_sessions WHERE owner_scope='fixture';") == 15000
                && Scalar(db, "SELECT ended_utc FROM voice_ime_sessions WHERE owner_scope='fixture';") is DBNull
                && (string?)Scalar(db, "SELECT state FROM voice_ime_sessions WHERE owner_scope='other-desktop';") == "recording",
                "异常中断恢复伪造时长/结束或影响其他桌面owner");
            Write("WriteSession", session);
            Check((string?)Scalar(db, "SELECT state FROM voice_ime_sessions WHERE owner_scope='fixture';") == "interrupted", "重放旧检查点复活了interrupted会话");
        }
        // 现有v7升级由同一幂等DDL建立新表，不删旧业务数据。
        using var upgrade = Database("upgrade"); upgrade.EnsureCreated();
        using (var c = upgrade.CreateConnection())
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = "DROP TABLE voice_ime_utterances; DROP TABLE voice_ime_sessions; UPDATE _schema_version SET version=7;";
            cmd.ExecuteNonQuery();
        }
        using var v8 = new SqliteDbService(upgrade.DatabasePath, 1); v8.EnsureCreated();
        Check(v8.SchemaVersion == 8 && Number(v8, "SELECT COUNT(*) FROM voice_ime_utterances;") == 0, "v7升级失败");
        Note("USAGE independent SQLite initializers/writers; bounded timeout + WAL; production Store duplicate replay; interrupted checkpoint lower bound; owner scope; v7 upgrade PASS");
    }

    private static async Task FailureAsync()
    {
        using var db = Database("failure");
        await using var usage = new VoiceImeUsageService(() => db, "fixture"); await Flush(usage);
        var clock = new ManualClock(); var voice = new Voice();
        await using var f = new Fixture("usage-failure", clock, usage, voice);
        await using var p = await f.ConnectAsync(101); await p.ExpectAsync('I', 0, 0, "");
        f.Foreground = 101; await f.BindAsync(p, 1); await f.Ui.WaitHistoryAsync(false, true);
        await p.SendAsync('T', 1); await p.ExpectAsync('C', 1, 0, ""); await p.ExpectAsync('R', 1, 0, ""); await f.ShowAsync(p, 1); await Flush(usage);
        using (var blocker = db.CreateConnection())
        using (var transaction = blocker.BeginTransaction(deferred: false))
        {
            f.Ui.Event(4, 1); await f.ShowAsync(p, 1);
            Check(usage.Status == new VoiceImeUsageStatus(false, false, true), "SQLite提交前未标记pending或假亮原文灯");
            await f.ShowAsync(p, 1); // 再过actor屏障，覆盖StatusChanged入队的反馈。
            Check(f.Ui.History == new VoiceImeUsageStatus(false, true), "pending被误当作最终存储不可用回传native");
            // writer 正等待数据库锁时，actor仍能处理定稿/停录。
            voice.Emit("合成锁等待定稿", true); await p.ExpectAsync('F', 1, 1, "合成锁等待定稿");
            await p.SendAsync('T', 1); await p.ExpectAsync('S', 2, 0, ""); await voice.Disposed.Task.WaitAsync(Deadline);
            var watch = Stopwatch.StartNew();
            Check(!await usage.FlushAsync().WaitAsync(Deadline), "锁超时应使统计不可用");
            await f.Ui.WaitHistoryAsync(false, false);
            Check(watch.Elapsed < TimeSpan.FromSeconds(4) && usage.Status == new VoiceImeUsageStatus(false, false)
                && !f.Host.IsCompleted && voice.Stops == 1, "SQLite失败杀语音owner、无限等待或开启原文");
            Check(!await usage.SetHistoryAsync(true).WaitAsync(Deadline)
                && !await usage.SetHistoryAsync(false).WaitAsync(Deadline)
                && usage.Status == new VoiceImeUsageStatus(false, false), "熔断后再次切换留下无限pending");
            transaction.Rollback();
        }
        Check(db.GetMeta("voice_ime.history_enabled") == null && Number(db, "SELECT COUNT(original_text) FROM voice_ime_utterances;") == 0,
            "失败的开启落盘或存了正文");
        await using (var loadFailure = new VoiceImeUsageService(() => throw new IOException(ErrorCanary), "fixture"))
        {
            Check(!await loadFailure.FlushAsync().WaitAsync(Deadline) && !await loadFailure.SetHistoryAsync(true).WaitAsync(Deadline)
                && loadFailure.Status == new VoiceImeUsageStatus(false, false), "加载失败未fail-closed");
        }
        using var disposedDb = Database("disposed");
        await using (var disposed = new VoiceImeUsageService(() => disposedDb, "fixture"))
        {
            await Flush(disposed);
            Check(await disposed.SetHistoryAsync(true).WaitAsync(Deadline), "释放fixture授权失败");
            await disposed.DisposeAsync();
            Check(!await disposed.SetHistoryAsync(true).WaitAsync(Deadline)
                && !await disposed.SetHistoryAsync(false).WaitAsync(Deadline)
                && disposed.Status == new VoiceImeUsageStatus(false, false), "已释放服务再次切换留下无限pending或仍可捕捉原文");
        }
        using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var overflowDb = Database("overflow");
        await using (var overflow = new VoiceImeUsageService(() => { ready.Set(); release.Wait(); return overflowDb; }, "fixture"))
        {
            try
            {
                Check(ready.Wait(Deadline), "队列fixture worker未开始");
                Check(overflow.Status == new VoiceImeUsageStatus(false, false), "加载期间不应误标设置pending");
                var id = overflow.BeginSession();
                for (var i = 0; i < 300; i++) overflow.Recognized(id, "合成", true);
                Check(overflow.Status == new VoiceImeUsageStatus(false, false) && !await overflow.FlushAsync(), "有界队列溢出未熔断");
            }
            finally { release.Set(); }
        }
        Note("USAGE real SQLite lock timeout: pending disables text without native feedback, final failure clears pending, audio actor responsive; faulted/disposed toggles, loading state and bounded overflow fail-closed PASS");
    }
}