using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TrueFluentPro.Services;

public enum VoiceImeUsageReportAvailability { Ready, DatabaseMissing, SchemaMissing }

public sealed record VoiceImeUsageReportDay(
    DateOnly Date, long Characters, long Utterances, long RecordingMilliseconds, long Sessions);

public sealed record VoiceImeUsageReportSession(
    string SessionId, DateTimeOffset CreatedAt, DateOnly CreatedLocalDate,
    long Characters, long Utterances, long RecordingMilliseconds,
    bool DurationBelongsToRange, string State, string? StopReason);

public enum VoiceImeSessionTextAvailability { Ready, DatabaseMissing, SchemaMissing, SessionMissing }

/// <summary>已保存的识别 source，不是目标应用全文；翻译模式下也不是译文输入。</summary>
public sealed record VoiceImeSessionTextEntry(
    long Sequence, DateTimeOffset FinalizedAt, DateOnly LocalDate, long Characters, string? OriginalText);

/// <summary>会话全部日期的一批识别记录；NULL 表示当时未保存，不受当前保存开关影响。</summary>
public sealed record VoiceImeSessionTextPage(
    string SessionId, VoiceImeSessionTextAvailability Availability,
    IReadOnlyList<VoiceImeSessionTextEntry> Entries, long NextSequence, bool HasMore);

public sealed record VoiceImeUsageReportHour(int Hour, long Characters, long Utterances);
public sealed record VoiceImeUsageReportBucket(string Key, string Label, long Count);
public sealed record VoiceImeUsageReportActiveRun(DateOnly StartDate, DateOnly EndDate)
{
    public int Days => EndDate.DayNumber - StartDate.DayNumber + 1;
}
public sealed record VoiceImeUsageReportPeriod(
    DateOnly StartDate, DateOnly EndDate, long Characters, long RecordingMilliseconds, long Sessions);

public sealed record VoiceImeUsageReportChange(long Current, long Previous)
{
    public decimal? Percentage => Previous == 0 ? null : ((decimal)Current - Previous) * 100 / Previous;
    public string Label => Previous == 0
        ? Current == 0 ? "持平 · 均为 0" : "从 0 开始"
        : Current == Previous ? "较上期持平"
        : "较上期 " + Percentage!.Value.ToString("+0.0;-0.0;0.0", CultureInfo.GetCultureInfo("zh-CN")) + "%";
}

/// <summary>
/// 字数/句数按定稿时保存的 local_date；会话数/全部录音时长按 created_local_date。
/// 明细包含范围内创建或有定稿的会话，字数仅限所选范围，时长不会拆分或推算。
/// </summary>
public sealed record VoiceImeUsageReport(
    DateOnly StartDate, DateOnly EndDate, DateTimeOffset CapturedAt,
    VoiceImeUsageReportAvailability Availability,
    IReadOnlyList<VoiceImeUsageReportDay> Days,
    IReadOnlyList<VoiceImeUsageReportSession> Sessions, long MatchingSessions)
{
    public long Characters => Days.Sum(day => day.Characters);
    public long Utterances => Days.Sum(day => day.Utterances);
    public long RecordingMilliseconds => Days.Sum(day => day.RecordingMilliseconds);
    public long SessionCount => Days.Sum(day => day.Sessions);
    public IReadOnlyList<VoiceImeUsageReportHour> Hours { get; init; } = Array.Empty<VoiceImeUsageReportHour>();
    public IReadOnlyList<VoiceImeUsageReportBucket> SentenceLengths { get; init; } = Array.Empty<VoiceImeUsageReportBucket>();
    // 创建日 cohort，与 SessionCount 一致；不是仅限已结束会话，也不受明细上限影响。
    public IReadOnlyList<VoiceImeUsageReportBucket> Outcomes { get; init; } = Array.Empty<VoiceImeUsageReportBucket>();
    public VoiceImeUsageReportPeriod? PreviousPeriod { get; init; }
    public int ActiveDays => Days.Count(day => day.Characters > 0);
    public decimal DailyAverageCharacters => Days.Count == 0 ? 0 : (decimal)Characters / Days.Count;
    // 仅计算选区内的连续有字日期，不越界接续，不把只有连接/零字句的日期算作活跃日。
    public IReadOnlyList<VoiceImeUsageReportActiveRun> ActiveRuns
    {
        get
        {
            var runs = new List<VoiceImeUsageReportActiveRun>();
            foreach (var day in Days.Where(day => day.Characters > 0).OrderBy(day => day.Date))
            {
                if (runs.Count > 0 && runs[^1].EndDate.DayNumber + 1 == day.Date.DayNumber)
                    runs[^1] = runs[^1] with { EndDate = day.Date };
                else runs.Add(new(day.Date, day.Date));
            }
            return runs;
        }
    }
    public int LongestActiveStreak => ActiveRuns.Count == 0 ? 0 : ActiveRuns.Max(run => run.Days);
}

/// <summary>
/// 报表专用只读查询。不创建 VoiceImeUsageService（其初始化会恢复/中断会话），
/// 不调用 SqliteDbService.CreateConnection/EnsureCreated（会迁移及写库）。
/// </summary>
public sealed class VoiceImeUsageReportService
{
    public const int SessionDetailLimit = 200;
    public const int SessionTextPageLimit = 100;
    private readonly string _databasePath;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _readGate = new(1, 1);

    // 与 SqliteDbService 默认构造、VoiceImeSharedHost 共用 PathManager 的 AppDataPath。
    public VoiceImeUsageReportService()
        : this(Path.Combine(PathManager.Instance.AppDataPath, "truefluentpro.db")) { }

    /// <summary>显式路径仅供隔离验证或已有数据库路径复用；构造不打开数据库。</summary>
    public VoiceImeUsageReportService(string databasePath, TimeProvider? clock = null)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>SQLite 同步读取放到后台，范围切换可取消；不阻塞 Avalonia UI。</summary>
    public async Task<VoiceImeUsageReport> ReadAsync(int days = 7, CancellationToken cancellationToken = default)
    {
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        var capturedAt = _clock.GetLocalNow();
        var end = DateOnly.FromDateTime(capturedAt.DateTime);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Read(end.AddDays(1 - days), end, capturedAt, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }

    /// <summary>
    /// 仅在用户请求详情时调用。按 sessionId 参数化读取全部日期，sequence 游标向前翻页；
    /// 每次最多 100 句，不读取配置、输入框或启动存储 writer。每批为独立只读快照。
    /// </summary>
    public async Task<VoiceImeSessionTextPage> ReadSessionTextAsync(string sessionId, long afterSequence = 0,
        int pageSize = SessionTextPageLimit, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
            throw new ArgumentException("请提供有效的会话标识。", nameof(sessionId));
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (pageSize is < 1 or > SessionTextPageLimit) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ReadSessionText(sessionId, afterSequence, pageSize, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _readGate.Release(); }
    }

    private VoiceImeSessionTextPage ReadSessionText(string sessionId, long afterSequence, int pageSize,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { File.GetAttributes(_databasePath); }
        catch (FileNotFoundException) { return Empty(VoiceImeSessionTextAvailability.DatabaseMissing); }
        catch (DirectoryNotFoundException) { return Empty(VoiceImeSessionTextAvailability.DatabaseMissing); }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 2
        }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('voice_ime_sessions','voice_ime_utterances');";
            if (Convert.ToInt64(schema.ExecuteScalar(), CultureInfo.InvariantCulture) != 2)
                return Empty(VoiceImeSessionTextAvailability.SchemaMissing);
        }
        using (var command = Query("SELECT 1 FROM voice_ime_sessions WHERE session_id=@session;"))
        {
            if (command.ExecuteScalar() == null) return Empty(VoiceImeSessionTextAvailability.SessionMissing);
        }

        var entries = new List<VoiceImeSessionTextEntry>(pageSize);
        using (var command = Query(@"
SELECT sequence_no, finalized_utc, local_date, utc_offset_minutes, character_count, original_text
FROM voice_ime_utterances WHERE session_id=@session AND sequence_no>@after
ORDER BY sequence_no LIMIT @limit;"))
        {
            command.Parameters.AddWithValue("@after", afterSequence);
            command.Parameters.AddWithValue("@limit", pageSize);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var finalizedAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.None).ToOffset(TimeSpan.FromMinutes(reader.GetInt32(3)));
                entries.Add(new(reader.GetInt64(0), finalizedAt, ParseDate(reader.GetString(2)),
                    reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }
        var nextSequence = entries.Count == 0 ? afterSequence : entries[^1].Sequence;
        bool hasMore;
        // 只探测下一条是否存在，不额外加载第 101 条正文。
        using (var command = Query("SELECT 1 FROM voice_ime_utterances WHERE session_id=@session AND sequence_no>@after LIMIT 1;"))
        {
            command.Parameters.AddWithValue("@after", nextSequence);
            hasMore = command.ExecuteScalar() != null;
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(sessionId, VoiceImeSessionTextAvailability.Ready, entries.ToArray(), nextSequence, hasMore);

        SqliteCommand Query(string sql)
        {
            token.ThrowIfCancellationRequested();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@session", sessionId);
            return command;
        }
        VoiceImeSessionTextPage Empty(VoiceImeSessionTextAvailability availability)
            => new(sessionId, availability, Array.Empty<VoiceImeSessionTextEntry>(), afterSequence, false);
    }

    private VoiceImeUsageReport Read(DateOnly start, DateOnly end, DateTimeOffset capturedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 缺库不创建文件/目录；权限问题不冒充空态，而是交由窗口显示错误态。
        try { File.GetAttributes(_databasePath); }
        catch (FileNotFoundException) { return Empty(VoiceImeUsageReportAvailability.DatabaseMissing); }
        catch (DirectoryNotFoundException) { return Empty(VoiceImeUsageReportAvailability.DatabaseMissing); }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        }.ConnectionString);
        connection.Open();
        // DEFERRED 只读快照：卡片、趋势及明细读取同一版本，不取得写锁。
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('voice_ime_sessions','voice_ime_utterances');";
            if (Convert.ToInt64(schema.ExecuteScalar(), CultureInfo.InvariantCulture) != 2)
                return Empty(VoiceImeUsageReportAvailability.SchemaMissing);
        }

        var daily = new Dictionary<DateOnly, DayTotals>();
        for (var date = start; date <= end; date = date.AddDays(1)) daily.Add(date, new());

        // 两张表独立分组：绝不将一对多句子 JOIN 后再次 SUM 会话时长。
        using (var command = Query(@"
SELECT local_date, SUM(character_count), COUNT(*)
FROM voice_ime_utterances WHERE local_date >= @start AND local_date <= @end
GROUP BY local_date;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var totals = daily[ParseDate(reader.GetString(0))];
                totals.Characters = reader.GetInt64(1);
                totals.Utterances = reader.GetInt64(2);
            }
        }
        using (var command = Query(@"
SELECT created_local_date, SUM(recording_ms), COUNT(*)
FROM voice_ime_sessions WHERE created_local_date >= @start AND created_local_date <= @end
GROUP BY created_local_date;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var totals = daily[ParseDate(reader.GetString(0))];
                totals.RecordingMilliseconds = reader.GetInt64(1);
                totals.Sessions = reader.GetInt64(2);
            }
        }

        var hours = Enumerable.Range(0, 24).Select(hour => new VoiceImeUsageReportHour(hour, 0, 0)).ToArray();
        // SQLite 先解析 UTC，再加每条记录自己的历史偏移；不用 localtime/当前时区。
        // 写入端 UTC 使用 O 格式。小时分组只需整秒，去掉小数秒以免毫秒舍入跨入下一小时。
        using (var command = Query(@"
    SELECT CAST(strftime('%H', substr(finalized_utc,1,19), printf('%+d minutes', utc_offset_minutes)) AS INTEGER),
       SUM(character_count), COUNT(*)
FROM voice_ime_utterances WHERE local_date >= @start AND local_date <= @end
GROUP BY 1;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var hour = reader.GetInt32(0);
                hours[hour] = new(hour, reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        // 0 字定稿（例如只有标点）单列，保证各桶之和等于全部定稿句数。
        var lengths = new[]
        {
            new VoiceImeUsageReportBucket("zero", "0 字", 0), new("short", "1–10 字", 0),
            new("medium", "11–30 字", 0), new("long", "31–60 字", 0), new("extended", "61+ 字", 0)
        };
        using (var command = Query(@"
SELECT CASE WHEN character_count=0 THEN 0 WHEN character_count<=10 THEN 1
            WHEN character_count<=30 THEN 2 WHEN character_count<=60 THEN 3 ELSE 4 END, COUNT(*)
FROM voice_ime_utterances WHERE local_date >= @start AND local_date <= @end GROUP BY 1;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var index = reader.GetInt32(0);
                lengths[index] = lengths[index] with { Count = reader.GetInt64(1) };
            }
        }

        var outcomes = new[]
        {
            new VoiceImeUsageReportBucket("manual_stop", "手动停止", 0), new("inactivity_timeout", "无字超时", 0),
            new("other_ime", "切换输入法", 0), new("failed", "语音失败", 0), new("owner_exit", "后台退出", 0),
            new("unfinished", "未完成", 0), new("interrupted", "异常中断", 0), new("other", "其他 / 未知", 0)
        };
        using (var command = Query(@"
SELECT state, stop_reason, COUNT(*) FROM voice_ime_sessions
WHERE created_local_date >= @start AND created_local_date <= @end GROUP BY state, stop_reason;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var state = reader.GetString(0);
                var reason = reader.IsDBNull(1) ? null : reader.GetString(1);
                var key = state switch
                {
                    "connecting" or "recording" => "unfinished",
                    "interrupted" => "interrupted",
                    "ended" when reason is "manual_stop" or "inactivity_timeout" or "other_ime" or "failed" or "owner_exit" => reason,
                    _ => "other"
                };
                var index = Array.FindIndex(outcomes, bucket => bucket.Key == key);
                outcomes[index] = outcomes[index] with { Count = outcomes[index].Count + reader.GetInt64(2) };
            }
        }

        var previousStart = start.AddDays(-(end.DayNumber - start.DayNumber + 1));
        var previousEnd = start.AddDays(-1);
        long previousCharacters, previousMilliseconds, previousSessions;
        using (var command = Query(@"
SELECT COALESCE(SUM(character_count),0) FROM voice_ime_utterances
WHERE local_date >= @start AND local_date <= @end;", previousStart, previousEnd))
            previousCharacters = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        using (var command = Query(@"
SELECT COALESCE(SUM(recording_ms),0), COUNT(*) FROM voice_ime_sessions
WHERE created_local_date >= @start AND created_local_date <= @end;", previousStart, previousEnd))
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            previousMilliseconds = reader.GetInt64(0);
            previousSessions = reader.GetInt64(1);
        }

        var sessions = new List<VoiceImeUsageReportSession>();
        long matchingSessions = 0;
        // 先按 session_id 汇总所选日期定稿；JOIN 两侧均一行/会话。
        // COUNT OVER 在 LIMIT 前计算总数。跨日旧会话也出现在明细中，时长仍归创建日。
        using (var command = Query(@"
WITH finalized AS (
 SELECT session_id, SUM(character_count) AS characters, COUNT(*) AS utterances
 FROM voice_ime_utterances WHERE local_date >= @start AND local_date <= @end GROUP BY session_id
)
SELECT s.session_id, s.created_utc, s.created_local_date, s.created_offset_minutes,
       COALESCE(f.characters,0), COALESCE(f.utterances,0), s.recording_ms,
       s.state, s.stop_reason, COUNT(*) OVER()
FROM voice_ime_sessions s LEFT JOIN finalized f ON f.session_id=s.session_id
WHERE (s.created_local_date >= @start AND s.created_local_date <= @end) OR f.session_id IS NOT NULL
ORDER BY s.created_utc DESC, s.session_id DESC LIMIT @limit;"))
        {
            command.Parameters.AddWithValue("@limit", SessionDetailLimit);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var createdDate = ParseDate(reader.GetString(2));
                var createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture,
                    DateTimeStyles.None).ToOffset(TimeSpan.FromMinutes(reader.GetInt32(3)));
                sessions.Add(new(reader.GetString(0), createdAt, createdDate, reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), createdDate >= start && createdDate <= end,
                    reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
                matchingSessions = reader.GetInt64(9);
            }
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(start, end, capturedAt, VoiceImeUsageReportAvailability.Ready,
            daily.Select(pair => new VoiceImeUsageReportDay(pair.Key, pair.Value.Characters,
                pair.Value.Utterances, pair.Value.RecordingMilliseconds, pair.Value.Sessions)).ToArray(),
            sessions.ToArray(), matchingSessions)
        {
            Hours = hours, SentenceLengths = lengths, Outcomes = outcomes,
            PreviousPeriod = new(previousStart, previousEnd, previousCharacters, previousMilliseconds, previousSessions)
        };

        SqliteCommand Query(string sql, DateOnly? from = null, DateOnly? to = null)
        {
            token.ThrowIfCancellationRequested();
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("@start", (from ?? start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@end", (to ?? end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return command;
        }

        VoiceImeUsageReport Empty(VoiceImeUsageReportAvailability availability)
            => new(start, end, capturedAt, availability, Array.Empty<VoiceImeUsageReportDay>(),
                Array.Empty<VoiceImeUsageReportSession>(), 0);
    }

    private static DateOnly ParseDate(string date) => DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class DayTotals
    {
        public long Characters, Utterances, RecordingMilliseconds, Sessions;
    }
}