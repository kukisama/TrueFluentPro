using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Services;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.Views;

// 从仓库根执行独立测试：dotnet run --project tools/VoiceImeUsageReport.Tests -c Release
// 追加 -- --preview 可打开合成数据窗口；不读取真实数据库，不启动录音或 VoiceImeUsageService。
// 追加 -- --verify-ui 验证源码链接的实际窗口、小时切换、760宽和请求竞态；不依赖主项目构建。
internal static class ReportTests
{
    internal static readonly TimeProvider Clock = new FixedClock();
    internal static string PreviewDatabase = "";
    private static string TextDatabase = "";
    private const string SavedText = "历史识别原文：你好，世界 🌍\n第二行";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            VerifyAsync().GetAwaiter().GetResult();
            if (args.Contains("--verify-ui")) VerifyWindow();
            Console.WriteLine("PASS: report summaries and explicit session source-text reader; bounded paging, NULL history, parameter isolation, cancellation and read-only state.");
            if (args.Contains("--preview"))
                AppBuilder.Configure<PreviewApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task VerifyAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var missingPath = Path.Combine(root, "not-created", "usage.db");
        var missing = await new VoiceImeUsageReportService(missingPath, Clock).ReadAsync();
        Require(missing.Availability == VoiceImeUsageReportAvailability.DatabaseMissing
            && !Directory.Exists(Path.GetDirectoryName(missingPath)), "缺库查询创建了文件或目录");

        var path = Path.Combine(root, "usage.db");
        PreviewDatabase = path;
        using var database = new SqliteDbService(path, 1, false);
        database.EnsureCreated(); // 仅 fixture writer 调用生产 DDL；报表本身不持有数据库服务。
        var service = new VoiceImeUsageReportService(path, Clock);
        var empty = await service.ReadAsync();
        Require(empty.Availability == VoiceImeUsageReportAvailability.Ready && empty.Days.Count == 7
            && empty.Characters == 0 && empty.SessionCount == 0 && empty.MatchingSessions == 0,
            "新库空态/日期补零错误");
        using var writer = database.CreateConnection();
        SeedSession(writer, "midnight", "2026-11-05", 120000, "recording");
        SeedUtterance(writer, "midnight", 1, "2026-11-05", 10);
        SeedUtterance(writer, "midnight", 2, "2026-11-06", 20);
        SeedUtterance(writer, "midnight", 3, "2026-11-06", 30);
        SeedSession(writer, "old", "2026-10-07", 90000, "ended");
        SeedUtterance(writer, "old", 1, "2026-10-07", 999);
        SeedUtterance(writer, "old", 2, "2026-11-06", 5);
        SeedSession(writer, "boundary", "2026-10-08", 30000, "ended");
        SeedUtterance(writer, "boundary", 1, "2026-10-08", 70);
        SeedSession(writer, "today", "2026-11-06", 60000, "ended");
        SeedUtterance(writer, "today", 1, "2026-11-06", 100);
        SeedUtterance(writer, "today", 2, "2026-11-06", 150);
        SeedSession(writer, "connecting", "2026-11-06", 0, "connecting");
        SeedSession(writer, "interrupted", "2026-11-06", 120000, "interrupted");
        SeedUtterance(writer, "interrupted", 1, "2026-11-06", 40);
        Execute(writer, "INSERT INTO _meta(key,value) VALUES ('voice_ime.history_enabled','1');");

        var stateBefore = State(writer);
        var versionBefore = Scalar(writer, "PRAGMA data_version;");
        var hashBefore = Hash(path);
        var today = await service.ReadAsync(1);
        Require(today.StartDate == new DateOnly(2026, 11, 6) && today.EndDate == today.StartDate,
            "未按提供时钟的本地自然日筛选");
        Require(today.Characters == 345 && today.Utterances == 6 && today.SessionCount == 3
            && today.RecordingMilliseconds == 180000 && today.MatchingSessions == 5,
            "今日汇总错误：多句JOIN放大时长或跨日归属错误");
        var midnight = today.Sessions.Single(row => row.SessionId == "midnight");
        Require(midnight.Characters == 50 && midnight.Utterances == 2 && !midnight.DurationBelongsToRange
            && midnight.RecordingMilliseconds == 120000 && midnight.State == "recording"
            && midnight.CreatedAt.Offset == TimeSpan.FromHours(8), "跨午夜明细/历史偏移/检查点错误");
        Require(today.Sessions.Single(row => row.SessionId == "connecting").RecordingMilliseconds == 0,
            "连接未成功的会话被推算了录音时长");
        var week = await service.ReadAsync(7);
        Require(week.StartDate == new DateOnly(2026, 10, 31) && week.Days.Count == 7
            && week.Characters == 355 && week.RecordingMilliseconds == 300000 && week.SessionCount == 4,
            "7天范围或时长聚合错误");
        Require(week.Days.Single(day => day.Date == new DateOnly(2026, 11, 5)).RecordingMilliseconds == 120000
            && week.Days.Take(5).All(day => day.Characters == 0), "跨日时长拆到了定稿日或漏补零");
        var month = await service.ReadAsync(30);
        Require(month.StartDate == new DateOnly(2026, 10, 8) && month.Days.Count == 30
            && month.Characters == 425 && month.RecordingMilliseconds == 330000 && month.SessionCount == 5,
            "30天边界（含今日）或范围外记录错误");
        Require(State(writer) == stateBefore && Equals(Scalar(writer, "PRAGMA data_version;"), versionBefore)
            && Hash(path) == hashBefore && (string?)Scalar(writer, "SELECT value FROM _meta WHERE key='voice_ime.history_enabled';") == "1",
            "报表读取改变了会话/版本/数据库/隐私开关");
        Console.WriteLine("PASS: exact 1/7/30-day totals; separate session duration; checkpoint and offset; unchanged DB state/data_version/hash.");

        using (var transaction = writer.BeginTransaction())
        {
            SeedUtterance(writer, "today", 3, "2026-11-06", 7, transaction);
            var duringWrite = await service.ReadAsync(1);
            Require(duringWrite.Characters == 345, "WAL读取看到未提交定稿或阻塞写者");
            transaction.Commit();
        }
        Require((await service.ReadAsync(1)).Characters == 352, "刷新未看见已提交的新定稿");
        using (var transaction = writer.BeginTransaction())
        {
            for (var index = 0; index < 205; index++)
                SeedSession(writer, $"limit-{index:000}", "2026-11-06", 0, "ended", transaction);
            transaction.Commit();
        }
        var limited = await service.ReadAsync(1);
        Require(limited.Sessions.Count == VoiceImeUsageReportService.SessionDetailLimit
            && limited.MatchingSessions == 210 && limited.SessionCount == 208
            && limited.Characters == 352 && limited.RecordingMilliseconds == 180000,
            "明细限流截断了全部汇总或错误总数");
        Require(limited.Hours.Sum(hour => hour.Characters) == 352 && limited.Hours.Sum(hour => hour.Utterances) == 7
            && limited.SentenceLengths.Sum(bucket => bucket.Count) == 7
            && limited.Outcomes.Sum(bucket => bucket.Count) == 208,
            "新增维度受到200条明细截断影响");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try { await service.ReadAsync(7, cts.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: concurrent uncommitted writer isolation; refresh sees commit; bounded details retain full totals; cancellation.");

        var legacyPath = Path.Combine(root, "legacy.db");
        using (var legacy = Open(legacyPath))
            Execute(legacy, "CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES('retained');");
        var legacyHash = Hash(legacyPath);
        var legacyReport = await new VoiceImeUsageReportService(legacyPath, Clock).ReadAsync();
        Require(legacyReport.Availability == VoiceImeUsageReportAvailability.SchemaMissing && Hash(legacyPath) == legacyHash,
            "旧库无表被迁移或伪报正常空态");
        var invalidPath = Path.Combine(root, "invalid.db");
        File.WriteAllText(invalidPath, "synthetic-invalid-sqlite");
        try
        {
            await new VoiceImeUsageReportService(invalidPath, Clock).ReadAsync();
            throw new Exception("损坏数据库被伪报空态");
        }
        catch (SqliteException) { }

        var repository = FindRepository();
        var source = File.ReadAllText(Path.Combine(repository, "Services", "VoiceImeUsageReportService.cs"));
        var summaryStart = source.IndexOf("private VoiceImeUsageReport Read(", StringComparison.Ordinal);
        var summaryEnd = source.IndexOf("private static DateOnly ParseDate", summaryStart, StringComparison.Ordinal);
        var summarySource = source[summaryStart..summaryEnd];
        Require(!summarySource.Contains("original_text", StringComparison.OrdinalIgnoreCase)
            && !summarySource.Contains("ReadSessionText", StringComparison.Ordinal)
            && !source.Contains("SELECT *", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("new VoiceImeUsageService", StringComparison.Ordinal)
            && !source.Contains("new SqliteDbService", StringComparison.Ordinal)
            && !source.Contains(".CreateConnection(", StringComparison.Ordinal), "汇总读取正文或报表引入写服务");
        Require(typeof(VoiceImeUsageWindow).GetConstructor(Type.EmptyTypes) is { IsPublic: true },
            "缺少公共无参窗口构造");
        await VerifyDimensionsAsync(root);
        await VerifySessionTextAsync(root);
        Console.WriteLine("PASS: missing DB/schema never created/migrated; malformed DB raises error; summary has no body dependency/SELECT*/writer; public Window().");
    }

    private static async Task VerifySessionTextAsync(string root)
    {
        TextDatabase = Path.Combine(root, "session-text.db");
        using var database = new SqliteDbService(TextDatabase, 1, false);
        database.EnsureCreated();
        using var writer = database.CreateConnection();
        SeedSession(writer, "text-session", "2026-11-06", 10000, "ended", reason: "manual_stop");
        SeedSession(writer, "empty-text", "2026-11-05", 0, "connecting");
        SeedSession(writer, "null-text", "2026-11-04", 1000, "ended");
        SeedUtterance(writer, "null-text", 1, "2026-11-04", 3, text: null);
        SeedSession(writer, "other-text", "2026-10-01", 1000, "ended");
        SeedUtterance(writer, "other-text", 1, "2026-10-01", 8, text: "其他会话的识别原文");
        using (var transaction = writer.BeginTransaction())
        {
            // 逆序插入、有序号间隙；头两条在30天范围外，仍必须按会话返回。
            for (var index = 205; index >= 1; index--)
                SeedUtterance(writer, "text-session", index * 2, index <= 3 ? "2026-09-01" : "2026-11-06",
                    42, transaction, offset: index == 1 ? 345 : 480,
                    text: index switch { 1 => SavedText, 2 => null, 3 => "", _ => $"识别原文第 {index * 2} 句" });
            transaction.Commit();
        }
        Execute(writer, "INSERT INTO _meta(key,value) VALUES ('voice_ime.history_enabled','0');");
        var before = TextSnapshot(writer);
        var service = new VoiceImeUsageReportService(TextDatabase, Clock);
        var first = await service.ReadSessionTextAsync("text-session");
        Require(first.Availability == VoiceImeSessionTextAvailability.Ready && first.Entries.Count == 100
            && first.Entries[0].OriginalText == SavedText && first.Entries[1].OriginalText == null
            && first.Entries[2].OriginalText == "" && first.NextSequence == 200 && first.HasMore
            && first.Entries[0].Characters == 42 && first.Entries[0].LocalDate == new DateOnly(2026, 9, 1)
            && first.Entries[0].FinalizedAt.Offset == TimeSpan.FromMinutes(345)
            && first.Entries[0].FinalizedAt.Hour == 9 && first.Entries[0].FinalizedAt.Minute == 45,
            "正文、NULL/空串、历史时间/字数、关闭开关或首批上限错误");
        var second = await service.ReadSessionTextAsync("text-session", first.NextSequence);
        var last = await service.ReadSessionTextAsync("text-session", second.NextSequence);
        Require(second.Entries.Count == 100 && second.NextSequence == 400 && second.HasMore
            && last.Entries.Count == 5 && last.NextSequence == 410 && !last.HasMore
            && first.Entries.Concat(second.Entries).Concat(last.Entries).Select(row => row.Sequence)
                .SequenceEqual(Enumerable.Range(1, 205).Select(index => (long)index * 2)), "游标分页漏句/重句/次序错误");
        Require((await service.ReadSessionTextAsync("text-session", last.NextSequence)).Entries.Count == 0
            && (await service.ReadSessionTextAsync("text-session", pageSize: 1)).Entries.Count == 1
            && (await service.ReadSessionTextAsync("empty-text")).Availability == VoiceImeSessionTextAvailability.Ready
            && (await service.ReadSessionTextAsync("empty-text")).Entries.Count == 0
            && (await service.ReadSessionTextAsync("null-text")).Entries.Single().OriginalText == null,
            "空会话、全NULL、末页或小批次错误");
        var injection = await service.ReadSessionTextAsync("x' OR 1=1; DROP TABLE voice_ime_utterances;--");
        Require(injection.Availability == VoiceImeSessionTextAvailability.SessionMissing && injection.Entries.Count == 0,
            "会话参数注入泄露其他会话正文或执行SQL");
        await Reject<ArgumentException>(() => service.ReadSessionTextAsync(" "));
        await Reject<ArgumentOutOfRangeException>(() => service.ReadSessionTextAsync("text-session", -1));
        await Reject<ArgumentOutOfRangeException>(() => service.ReadSessionTextAsync("text-session", pageSize: 0));
        await Reject<ArgumentOutOfRangeException>(() => service.ReadSessionTextAsync("text-session", pageSize: 101));
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Reject<OperationCanceledException>(() => service.ReadSessionTextAsync("text-session", cancellationToken: cts.Token));
        }
        var missing = Path.Combine(root, "text-not-created", "missing.db");
        Require((await new VoiceImeUsageReportService(missing).ReadSessionTextAsync("text-session")).Availability
            == VoiceImeSessionTextAvailability.DatabaseMissing && !Directory.Exists(Path.GetDirectoryName(missing)),
            "详情缺库创建了文件");
        Require((await new VoiceImeUsageReportService(Path.Combine(root, "legacy.db"))
                .ReadSessionTextAsync("text-session")).Availability == VoiceImeSessionTextAvailability.SchemaMissing,
            "详情旧库没有明确缺表态");
        await Reject<SqliteException>(() => new VoiceImeUsageReportService(Path.Combine(root, "invalid.db"))
            .ReadSessionTextAsync("text-session"));
        Require(TextSnapshot(writer) == before, "原文详情改变DB/WAL/会话/开关/data_version");
        Execute(writer, "UPDATE _meta SET value='1' WHERE key='voice_ime.history_enabled';");
        var on = await service.ReadSessionTextAsync("text-session");
        Require(on.Entries.SequenceEqual(first.Entries), "现在开启开关补回了历史NULL或改变原文");
        Execute(writer, "UPDATE _meta SET value='0' WHERE key='voice_ime.history_enabled';");
        Console.WriteLine("PASS: source text preserved while history off; all session dates; 100/100/5 ordered keyset pages; historical offset/count; NULL vs empty; injection rejected; invalid arguments/cancellation; missing/schema/error; DB+WAL+state+meta+data_version unchanged.");

        async Task Reject<T>(Func<Task<VoiceImeSessionTextPage>> action) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new Exception("未拒绝无效原文查询：" + typeof(T).Name);
        }
    }

    private static (string? State, string Db, string Wal, long Version, string? Privacy) TextSnapshot(SqliteConnection writer)
        => (State(writer), Hash(TextDatabase), Hash(TextDatabase + "-wal"),
            Convert.ToInt64(Scalar(writer, "PRAGMA data_version;"), CultureInfo.InvariantCulture),
            (string?)Scalar(writer, "SELECT value FROM _meta WHERE key='voice_ime.history_enabled';"));

    private static async Task VerifyDimensionsAsync(string root)
    {
        var path = Path.Combine(root, "dimensions.db");
        using var database = new SqliteDbService(path, 1, false);
        database.EnsureCreated();
        using var writer = database.CreateConnection();
        SeedSession(writer, "before-previous", "2026-10-23", 999000, "ended");
        SeedUtterance(writer, "before-previous", 1, "2026-10-23", 999);
        SeedSession(writer, "previous-start", "2026-10-24", 60000, "ended");
        SeedUtterance(writer, "previous-start", 1, "2026-10-24", 100);
        SeedSession(writer, "previous-end", "2026-10-30", 90000, "ended");
        SeedUtterance(writer, "previous-end", 1, "2026-10-30", 200);
        SeedUtterance(writer, "previous-end", 2, "2026-11-01", 40); // 旧会话本期定稿
        SeedSession(writer, "start", "2026-10-31", 120000, "ended", reason: "manual_stop");
        SeedUtterance(writer, "start", 1, "2026-10-31", 1, utc: "2026-10-30T18:20:00.0000000+00:00", offset: 345);
        SeedUtterance(writer, "start", 2, "2026-10-31", 10, utc: "2026-11-01T03:59:59.9999999+00:00", offset: -240);
        SeedSession(writer, "active", "2026-11-01", 60000, "recording", reason: "failed");
        // 相同 UTC + 不同历史偏移；不能用当前 fixture 的 +8 或会话创建偏移代替。
        SeedUtterance(writer, "active", 1, "2026-11-01", 11, utc: "2026-11-01T00:30:00.0000000+00:00", offset: 60);
        SeedUtterance(writer, "active", 2, "2026-11-01", 30, utc: "2026-11-01T00:30:00.0000000+00:00", offset: 120);
        SeedSession(writer, "connecting-zero", "2026-11-02", 0, "connecting");
        SeedUtterance(writer, "connecting-zero", 1, "2026-11-02", 0);
        SeedSession(writer, "timeout", "2026-11-03", 30000, "ended", reason: "inactivity_timeout");
        SeedUtterance(writer, "timeout", 1, "2026-11-03", 31, utc: "2026-11-04T04:45:00.0000000+00:00", offset: -330);
        SeedSession(writer, "switch", "2026-11-04", 40000, "ended", reason: "other_ime");
        SeedUtterance(writer, "switch", 1, "2026-11-04", 60, utc: "2026-11-03T19:00:00.0000000+00:00", offset: 330);
        SeedSession(writer, "failed", "2026-11-05", 50000, "ended", reason: "failed");
        SeedUtterance(writer, "failed", 1, "2026-11-05", 61);
        SeedSession(writer, "exit", "2026-11-06", 60000, "ended", reason: "owner_exit");
        SeedUtterance(writer, "exit", 1, "2026-11-06", 100);
        SeedSession(writer, "interrupted", "2026-11-06", 70000, "interrupted", reason: "interrupted");
        SeedUtterance(writer, "interrupted", 1, "2026-11-06", 0);
        SeedSession(writer, "unknown", "2026-11-06", 80000, "ended", reason: "future_reason");
        Execute(writer, "INSERT INTO _meta(key,value) VALUES ('voice_ime.history_enabled','0'); UPDATE voice_ime_utterances SET original_text=NULL WHERE sequence_no=2;");

        var service = new VoiceImeUsageReportService(path, Clock);
        Console.WriteLine("HOUR_PRECISION: raw=" + Scalar(writer,
            "SELECT strftime('%H','2026-11-01T03:59:59.9999999+00:00','-240 minutes');") + "; expected=23; report query uses UTC whole seconds.");
        var before = (State: State(writer), Db: Hash(path), Wal: Hash(path + "-wal"),
            Version: Convert.ToInt64(Scalar(writer, "PRAGMA data_version;"), CultureInfo.InvariantCulture));
        var week = await service.ReadAsync(7);
        Require(week.Characters == 344 && week.Utterances == 11 && week.SessionCount == 9
            && week.RecordingMilliseconds == 510000 && week.MatchingSessions == 10,
            "多维fixture基线/跨日cohort错误");
        Require(week.Hours.Count == 24 && week.Hours.Select(hour => hour.Hour).SequenceEqual(Enumerable.Range(0, 24))
            && week.Hours[0] == new VoiceImeUsageReportHour(0, 61, 2)
            && week.Hours[1] == new VoiceImeUsageReportHour(1, 11, 1)
            && week.Hours[2] == new VoiceImeUsageReportHour(2, 30, 1)
            && week.Hours[23] == new VoiceImeUsageReportHour(23, 41, 2)
            && week.Hours[12] == new VoiceImeUsageReportHour(12, 201, 5)
            && week.Hours.Sum(hour => hour.Characters) == week.Characters
            && week.Hours.Sum(hour => hour.Utterances) == week.Utterances,
            "历史偏移、半小时/45分钟、UTC跨日筛选或24小时补零错误");
        Require(week.SentenceLengths.Select(bucket => bucket.Count).SequenceEqual(new long[] { 2, 2, 2, 3, 2 }),
            "句长0/1/10/11/30/31/60/61边界或零字句处理错误");
        Require(week.Outcomes.Select(bucket => bucket.Count).SequenceEqual(new long[] { 1, 1, 1, 1, 1, 2, 1, 1 }),
            "结束原因被合并、未完成按残留reason误判、或混入范围外创建会话");
        Require(week.ActiveDays == 6 && week.LongestActiveStreak == 4 && week.DailyAverageCharacters == 344m / 7
            && week.ActiveRuns.SequenceEqual(new[]
            {
                new VoiceImeUsageReportActiveRun(new(2026, 10, 31), new(2026, 11, 1)),
                new VoiceImeUsageReportActiveRun(new(2026, 11, 3), new(2026, 11, 6))
            }), "零字日/选区连续段/日均全自然日分母错误");
        Require(week.PreviousPeriod == new VoiceImeUsageReportPeriod(new(2026, 10, 24), new(2026, 10, 30), 300, 150000, 2)
            && new VoiceImeUsageReportChange(344, 300).Label == "较上期 +14.7%",
            "上期不是紧邻等长自然日或跨日/范围外数据混入");
        var today = await service.ReadAsync(1);
        Require(today.PreviousPeriod == new VoiceImeUsageReportPeriod(new(2026, 11, 5), new(2026, 11, 5), 61, 50000, 1)
            && today.ActiveRuns.Single().Days == 1, "今日上期应是昨日完整自然日，连续段不可越界");
        var month = await service.ReadAsync(30);
        Require(month.PreviousPeriod == new VoiceImeUsageReportPeriod(new(2026, 9, 8), new(2026, 10, 7), 0, 0, 0)
            && new VoiceImeUsageReportChange(month.Characters, 0).Label == "从 0 开始"
            && new VoiceImeUsageReportChange(month.RecordingMilliseconds, 0).Percentage == null
            && new VoiceImeUsageReportChange(month.SessionCount, 0).Label == "从 0 开始"
            && new VoiceImeUsageReportChange(0, 0).Label == "持平 · 均为 0"
            && new VoiceImeUsageReportChange(0, 100).Percentage == -100,
            "上期零基数未安全区分从0开始/均为0/降到0");
        var after = (State: State(writer), Db: Hash(path), Wal: Hash(path + "-wal"),
            Version: Convert.ToInt64(Scalar(writer, "PRAGMA data_version;"), CultureInfo.InvariantCulture));
        Console.WriteLine($"READONLY: state={before.State == after.State}; db={before.Db == after.Db}; wal={before.Wal == after.Wal}; data_version={before.Version}->{after.Version}.");
        Require(before == after
            && (string?)Scalar(writer, "SELECT value FROM _meta WHERE key='voice_ime.history_enabled';") == "0",
            "多维查询修改DB/WAL/会话检查点/隐私开关");
        // 额外的隐私对照：fixture 隐藏正文列后仍能完整查询，证明新增分组不依赖正文。
        Execute(writer, "ALTER TABLE voice_ime_utterances RENAME COLUMN original_text TO unavailable_body;");
        var bodyless = await service.ReadAsync(7);
        Require(bodyless.Hours.SequenceEqual(week.Hours) && bodyless.SentenceLengths.SequenceEqual(week.SentenceLengths)
            && bodyless.Outcomes.SequenceEqual(week.Outcomes) && bodyless.PreviousPeriod == week.PreviousPeriod,
            "隐藏fixture正文列后新增维度发生变化或无法查询");
        Console.WriteLine("PASS: 24 historical local hours incl ±fractional offsets/midnight; all length boundaries; 8 distinct outcomes; active runs and zero-day average; exact previous natural dates/zero changes; unchanged DB+WAL; body column unavailable.");
    }

    // 可选真实窗口验证仍复用本项目；源码链接保证验的是本轮报表，不要求另一 agent 主构建。
    private static void VerifyWindow()
    {
        AppBuilder.Configure<PreviewApplication>().UsePlatformDetect().SetupWithoutStarting();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var loop = new CancellationTokenSource();
        using var timeout = deadline.Token.Register(() => loop.Cancel());
        Exception? failure = null;
        var owned = new List<Window>();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var before = Hash(PreviewDatabase);
                var service = new VoiceImeUsageReportService(PreviewDatabase, Clock);
                var window = new VoiceImeUsageWindow(service);
                owned.Add(window);
                window.Show();
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled
                    && FindOptional<UniformGrid>(window, "UsageHourlyChart")?.Children.Count == 24);
                Require(window.TryGetPlatformHandle() is { Handle: var handle } && handle != IntPtr.Zero, "未创建真实平台窗口");
                Require(Find<TextBlock>(window, "UsageCharacters").Text == "362"
                    && Find<TextBlock>(window, "UsageActiveDays").Text == "2 / 7 天"
                    && Find<TextBlock>(window, "UsageActiveStreak").Text == "2 天 · 1 段"
                    && Find<TextBlock>(window, "UsageDailyAverage").Text == "51.7 字",
                    "实际窗口卡片/活跃段/日均呈现错误");
                Require(Find<TextBlock>(window, "UsageComparisonCaption").Text!.Contains("2026.10.24 — 2026.10.30")
                    && Find<TextBlock>(window, "UsageCharactersComparison").Text == "从 0 开始",
                    "上期日期/零基数文案错误");

                // 持有真实查询串行门：旧请求一定排队，快速切今日/30天后只允许30天呈现。
                var gate = (SemaphoreSlim)typeof(VoiceImeUsageReportService)
                    .GetField("_readGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                await gate.WaitAsync(deadline.Token);
                try
                {
                    Click(Find<Button>(window, "UsageRange1"));
                    Click(Find<Button>(window, "UsageRange30"));
                    await Render(window);
                    Require(!Find<TextBlock>(window, "UsageCharacters").IsEffectivelyVisible,
                        "排队切换期间把旧快照标为新范围");
                }
                finally { gate.Release(); }
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled
                    && FindOptional<UniformGrid>(window, "UsageDailyChart")?.Children.Count == 30);
                Require(Find<TextBlock>(window, "UsageCharacters").Text == "432", "旧请求覆盖了最新范围");
                Click(Find<Button>(window, "UsageRange7"));
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled
                    && FindOptional<UniformGrid>(window, "UsageDailyChart")?.Children.Count == 7);
                Click(Find<Button>(window, "UsageRefresh"));
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled);

                window.Width = 760;
                window.Height = 600;
                await Render(window);
                Require(Math.Abs(window.ClientSize.Width - 760) < 1, "报表未保持760最小客户区宽");
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
                Require(scroll.Extent.Width <= scroll.Viewport.Width + 1 && scroll.Extent.Height > scroll.Viewport.Height,
                    "最小宽产生横向溢出或无法纵向滚动");
                var selector = Find<ComboBox>(window, "UsageHourlyMetric");
                ScrollTo(selector);
                await Render(window);
                Require(Visible(selector), "小时统计口径控件不可见");
                var hourChart = Find<UniformGrid>(window, "UsageHourlyChart");
                var tip = ToolTip.GetTip(hourChart.Children[12])?.ToString();
                Require(tip?.Contains("362 字 / 8 句") == true, "小时tooltip未同时保留字数/句数");
                selector.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down, Source = selector });
                await Render(window);
                Require(selector.SelectedIndex == 1 && Find<TextBlock>(window, "UsageHourlySummary").Text!.StartsWith("最高 8 句"),
                    "真实ComboBox方向键未切换小时句数图");
                var bar = (Border)((Border)((Grid)hourChart.Children[12]).Children[0]).Child!;
                Require(Math.Abs(bar.Height - 104) < .01, "句数峰值没有实际更新柱高");
                selector.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Up, Source = selector });
                await Render(window);
                Require(selector.SelectedIndex == 0 && Find<TextBlock>(window, "UsageHourlySummary").Text!.StartsWith("最高 362 字"),
                    "小时字数图无法切回");
                foreach (var name in new[] { "UsageLengthDistribution", "UsageOutcomeDistribution" })
                {
                    var panel = Find<StackPanel>(window, name);
                    ScrollTo(panel);
                    await Render(window);
                    foreach (var text in panel.GetVisualDescendants().OfType<TextBlock>())
                        Require(text.TextLayout.Width <= text.Bounds.Width + 1, "760宽新增维度文字溢出：" + text.Text);
                }
                var lastDefinition = window.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text?.StartsWith("进行中或异常中断会话只显示") == true);
                scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                await Render(window);
                Require(Visible(lastDefinition) && window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text?.Contains("关闭原文保存仍会统计全部维度") == true),
                    "统计口径底部不可达或遗漏无需原文授权说明");
                Require(Hash(PreviewDatabase) == before, "窗口切换/刷新/滚动修改了隔离数据库");
                window.Close();

                foreach (var (file, prefix) in new[]
                {
                    ("ui-not-created.db", "还没有本地用量数据库"), ("invalid.db", "暂时无法读取本地统计")
                })
                {
                    var path = Path.Combine(Path.GetDirectoryName(PreviewDatabase)!, file);
                    var stateWindow = new VoiceImeUsageWindow(new VoiceImeUsageReportService(path, Clock));
                    owned.Add(stateWindow);
                    stateWindow.Show();
                    await Wait(() => stateWindow.GetVisualDescendants().OfType<TextBlock>()
                        .Any(text => text.Text?.StartsWith(prefix) == true));
                    stateWindow.Close();
                }
                Console.WriteLine("PASS: source-linked actual Win32/Avalonia report; 760px layout/scroll; hourly routed-key toggle + bar/tooltip; active/comparison text; queued stale request rejected; refresh/missing/error/privacy states; DB unchanged. No main build/config/cloud/audio.");

                using var textWriter = Open(TextDatabase); // 只打开合成 fixture，核对只读状态。
                var textBefore = TextSnapshot(textWriter);
                service = new VoiceImeUsageReportService(TextDatabase, Clock);
                window = new VoiceImeUsageWindow(service);
                owned.Add(window);
                window.Show();
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled
                    && FindOptional<ListBox>(window, "UsageSessionList") != null);
                Require(window.TryGetPlatformHandle()?.Handle != IntPtr.Zero, "原文详情未创建真实窗口");
                scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
                var list = Find<ListBox>(window, "UsageSessionList");
                ScrollTo(list);
                await Render(window);
                SelectNext();
                await Render(window); // 隐藏详情显现后，等待 ScrollViewer 模板和内容进入实际视觉树。
                Require((list.SelectedItem as VoiceImeUsageReportSession)?.SessionId == "text-session"
                    && Rows().Children.Count == 0 && Find<Button>(window, "UsageViewSessionText").IsEnabled
                    && Status().StartsWith("尚未读取正文"), "真实选择未生效或选择时主动读取了正文");
                Click(Find<Button>(window, "UsageViewSessionText"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Rows().Children.Count == 100 && Rows().GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Name == "UsageSessionOriginalText" && text.Text == SavedText)
                    && Rows().GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "UsageSessionTextMissing")
                    && Rows().GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("09:45:00 +05:45 · 定稿 42 字") == true),
                    "routed查看未显示非空历史识别正文/NULL/时间字数");
                var original = Rows().GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == SavedText);
                ScrollTo(original);
                await Render(window);
                Require(Visible(original), "已保存原文未实际可见");
                Click(Find<Button>(window, "UsageSessionTextNext"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Rows().Children.Count == 100 && Status().Contains("202–400")
                    && !Rows().GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == SavedText),
                    "下一批没有替换旧正文或无界累积");
                Click(Find<Button>(window, "UsageSessionTextNext"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Rows().Children.Count == 5 && !Find<Button>(window, "UsageSessionTextNext").IsVisible,
                    "末批条数或更多按钮错误");

                gate = (SemaphoreSlim)typeof(VoiceImeUsageReportService)
                    .GetField("_readGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                await gate.WaitAsync(deadline.Token);
                try
                {
                    Click(Find<Button>(window, "UsageViewSessionText"));
                    Require(Status().StartsWith("正在读取") && Rows().Children.Count == 0, "缺少原文加载态");
                    Click(Find<Button>(window, "UsageSessionTextCancel"));
                    Require(Status().StartsWith("已取消") && Find<Button>(window, "UsageViewSessionText").IsEnabled,
                        "取消态或重试按钮错误");
                }
                finally { gate.Release(); }
                await Wait(TextIdle);
                Require(Status().StartsWith("已取消") && Rows().Children.Count == 0, "取消后迟到结果覆盖界面");

                await gate.WaitAsync(deadline.Token);
                try
                {
                    Click(Find<Button>(window, "UsageViewSessionText"));
                    SelectNext();
                    Require((list.SelectedItem as VoiceImeUsageReportSession)?.SessionId == "empty-text"
                        && Status().StartsWith("尚未读取正文"), "切选没有清除旧详情");
                }
                finally { gate.Release(); }
                await Wait(TextIdle);
                Require(Rows().Children.Count == 0 && Status().StartsWith("尚未读取正文"), "切选后旧会话正文迟到");
                Click(Find<Button>(window, "UsageViewSessionText"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Status() == "此会话暂无识别定稿记录。", "会话空态错误");
                SelectNext();
                Click(Find<Button>(window, "UsageViewSessionText"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Rows().Children.Count == 1 && Rows().GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "UsageSessionTextMissing").Text!.Contains("当时未保存"), "全NULL未解释历史未保存");
                await gate.WaitAsync(deadline.Token);
                try
                {
                    Click(Find<Button>(window, "UsageViewSessionText"));
                    Click(Find<Button>(window, "UsageSessionTextClose"));
                }
                finally { gate.Release(); }
                await Wait(TextIdle);
                Require(list.SelectedItem == null && Rows().Children.Count == 0
                    && !Find<Button>(window, "UsageViewSessionText").IsEnabled, "关闭详情后旧结果仍显示");
                SelectNext();
                await gate.WaitAsync(deadline.Token);
                try { Click(Find<Button>(window, "UsageViewSessionText")); window.Close(); }
                finally { gate.Release(); }
                await Wait(TextIdle);
                Require(Rows().Children.Count == 0 && TextSnapshot(textWriter) == textBefore,
                    "关闭窗口后有迟到正文或UI改变DB/WAL/开关/状态");

                // fixture 主动移走正文列：汇总/选择仍正常，点击详情才显示友好错误，不漏SQL/路径。
                Execute(textWriter, "ALTER TABLE voice_ime_utterances RENAME COLUMN original_text TO unavailable_body;");
                window = new VoiceImeUsageWindow(new VoiceImeUsageReportService(TextDatabase, Clock));
                owned.Add(window);
                window.Show();
                await Wait(() => Find<Button>(window, "UsageRefresh").IsEnabled
                    && FindOptional<ListBox>(window, "UsageSessionList") != null);
                list = Find<ListBox>(window, "UsageSessionList");
                SelectNext();
                Click(Find<Button>(window, "UsageViewSessionText"));
                await Wait(() => Find<Button>(window, "UsageViewSessionText").IsEnabled);
                Require(Status().StartsWith("暂时无法读取识别原文") && Rows().Children.Count == 0
                    && !Status().Contains(TextDatabase) && !Status().Contains("original_text"), "原文错误态泄露路径/SQL或残留正文");
                window.Close();
                Console.WriteLine("PASS: actual Win32/Avalonia routed ListBox selection + Enter view displays visible nonempty saved source/time/count; selection and summary never load bodies; 100/100/5 replacing pages; loading/cancel/empty/NULL/error; stale selection/detail-close/window-close rejected; DB+WAL+meta+state unchanged.");

                StackPanel Rows() => Find<StackPanel>(window, "UsageSessionTextRows");
                string Status() => Find<TextBlock>(window, "UsageSessionTextStatus").Text!;
                bool TextIdle() => typeof(VoiceImeUsageWindow).GetField("_textCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window) == null;
                void SelectNext()
                {
                    list.Focus();
                    // 清空选择仍可能保留末项导航锚点；Home 明确回首项，不修改 SelectedItem 来伪造选择。
                    var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                        Key = list.SelectedItem == null ? Key.Home : Key.Down, Source = list };
                    list.RaiseEvent(key);
                    Require(key.Handled && list.SelectedItem != null, "ListBox未处理真实路由方向键选择");
                }

                async Task Render(Window target)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (!target.IsVisible) return; // 关闭后只泵取消完成，不请求已关闭窗口的绘制。
                    target.UpdateLayout();
                    var visual = ElementComposition.GetElementVisual(target)!;
                    await Dispatcher.UIThread.AwaitWithPriority(visual.Compositor.RequestCompositionBatchCommitAsync().Rendered
                        .WaitAsync(TimeSpan.FromSeconds(5), deadline.Token), DispatcherPriority.Background);
                }
                async Task Wait(Func<bool> ready)
                {
                    while (!ready()) await Render(owned.Last());
                    await Render(owned.Last());
                }
                void ScrollTo(Control target)
                {
                    var origin = target.TranslatePoint(default, window)!.Value;
                    var viewport = scroll.TranslatePoint(default, window)!.Value;
                    scroll.Offset = new Vector(0, Math.Clamp(scroll.Offset.Y + origin.Y - viewport.Y - 12,
                        0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                }
                bool Visible(Control target)
                {
                    var point = target.TranslatePoint(default, window)!.Value;
                    var viewport = scroll.TranslatePoint(default, window)!.Value;
                    return target.IsEffectivelyVisible && point.X >= 0 && point.X + target.Bounds.Width <= window.ClientSize.Width + 1
                        && point.Y >= viewport.Y && point.Y + target.Bounds.Height <= viewport.Y + scroll.Viewport.Height + 1;
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                foreach (var window in owned.Where(window => window.IsVisible)) window.Close();
                loop.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(loop.Token);
        if (deadline.IsCancellationRequested) throw new TimeoutException("隔离报表窗口验证超时");
        if (failure != null) ExceptionDispatchInfo.Throw(failure);

        static void Click(Button button)
        {
            var input = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = button };
            button.RaiseEvent(input);
            Require(input.Handled, "Button未处理真实路由Enter");
        }
        static T? FindOptional<T>(Window root, string name) where T : Control
            => root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);
        static T Find<T>(Window root, string name) where T : Control
            => FindOptional<T>(root, name) ?? throw new Exception("缺少报表控件：" + name);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void SeedSession(SqliteConnection connection, string id, string date, long milliseconds, string state,
        SqliteTransaction? transaction = null, string? reason = null)
    {
        var local = DateTimeOffset.Parse(date + "T10:00:00+08:00", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO voice_ime_sessions(session_id,owner_scope,created_utc,created_local_date,created_offset_minutes,
recording_started_utc,checkpoint_utc,checkpoint_local_date,checkpoint_offset_minutes,ended_utc,recording_ms,state,stop_reason,revision)
VALUES(@id,'report-fixture',@utc,@date,480,@started,@utc,@date,480,NULL,@ms,@state,@reason,2);";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@date", date);
        command.Parameters.AddWithValue("@utc", local.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@started", state == "connecting" ? DBNull.Value : local.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@ms", milliseconds);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void SeedUtterance(SqliteConnection connection, string id, long sequence, string date, int count,
        SqliteTransaction? transaction = null, string? utc = null, int offset = 480,
        string? text = "SYNTHETIC_BODY_MUST_NOT_BE_READ")
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO voice_ime_utterances(session_id,sequence_no,finalized_utc,local_date,utc_offset_minutes,character_count,original_text)
VALUES(@id,@sequence,@utc,@date,@offset,@count,@text);";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@sequence", sequence);
        command.Parameters.AddWithValue("@date", date);
        command.Parameters.AddWithValue("@utc", utc ?? date + "T04:00:00.0000000+00:00");
        command.Parameters.AddWithValue("@offset", offset);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@text", (object?)text ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static string? State(SqliteConnection connection) => (string?)Scalar(connection,
        "SELECT group_concat(value,';') FROM (SELECT session_id||':'||state||':'||recording_ms||':'||revision||':'||checkpoint_utc AS value FROM voice_ime_sessions ORDER BY session_id);");
    private static string Hash(string path)
    {
        // fixture 保持 SQLite 写连接打开；只读校验不能反向拒绝该句柄的共享写访问。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TrueFluentPro.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("找不到报表源码，无法核验隐私查询合同");
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 6, 4, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Fixture+8", TimeSpan.FromHours(8), "Fixture+8", "Fixture+8");
    }
}

public sealed class PreviewApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new VoiceImeUsageWindow(new VoiceImeUsageReportService(ReportTests.PreviewDatabase, ReportTests.Clock));
        base.OnFrameworkInitializationCompleted();
    }
}