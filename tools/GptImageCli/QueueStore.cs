using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GptImageCli;

// Connections and commands are short-lived. No database transaction spans an HTTP request.
internal sealed class QueueStore
{
    private static readonly Lazy<bool> Provider = new(() =>
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        return true;
    });
    private readonly string _connectionString;
    public QueueStore(QueuePaths paths)
    {
        _ = Provider.Value;
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = paths.DatabaseFile, DefaultTimeout = 10, Pooling = false }.ToString();
        using var db = Open();
        using var version = Command(db, null, "PRAGMA user_version");
        if (Convert.ToInt32(version.ExecuteScalar()) is not (0 or 1))
            throw new CliException("队列数据库版本不兼容；请使用对应版本的 gpt-image，未修改任务。");
        Execute(db, null, "PRAGMA journal_mode=WAL;");
        using var tx = db.BeginTransaction();
        Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS jobs(
                id INTEGER PRIMARY KEY AUTOINCREMENT, queue TEXT NOT NULL, name TEXT NOT NULL,
                payload BLOB NOT NULL, state TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, next_at INTEGER NOT NULL DEFAULT 0,
                http_status INTEGER, error TEXT, result TEXT);
            CREATE INDEX IF NOT EXISTS jobs_dispatch ON jobs(queue,state,id);
            CREATE TABLE IF NOT EXISTS output_reservations(path TEXT COLLATE NOCASE PRIMARY KEY, job_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS attempts(
                job_id INTEGER NOT NULL, number INTEGER NOT NULL, queue TEXT NOT NULL, started_at INTEGER NOT NULL,
                finished_at INTEGER, http_status INTEGER, request_id TEXT, stage TEXT NOT NULL DEFAULT 'prepare', error TEXT,
                PRIMARY KEY(job_id,number));
            CREATE INDEX IF NOT EXISTS attempts_rate ON attempts(queue,started_at);
            CREATE TABLE IF NOT EXISTS queues(id TEXT PRIMARY KEY, paused INTEGER NOT NULL DEFAULT 0,
                next_at INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS control(id INTEGER PRIMARY KEY CHECK(id=1), paused INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO control(id) VALUES(1);
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, time INTEGER NOT NULL,
                job_id INTEGER, kind TEXT NOT NULL, message TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Execute(db, null, "PRAGMA synchronous=FULL;");
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var cmd = Command(db, tx, sql, values); return cmd.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    { using var cmd = Command(db, tx, sql, values); return Convert.ToInt64(cmd.ExecuteScalar()); }
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void Event(SqliteConnection db, SqliteTransaction? tx, long? job, string kind, string message) =>
        Execute(db, tx, "INSERT INTO events(time,job_id,kind,message) VALUES($time,$job,$kind,$message)",
            ("$time", Now), ("$job", job), ("$kind", kind), ("$message", message));

    public void Log(string kind, string message)
    { using var db = Open(); Event(db, null, null, kind, message); }

    public long Enqueue(string queue, string name, byte[] payload, IReadOnlyList<string>? outputs = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT OR IGNORE INTO queues(id) VALUES($q)", ("$q", queue));
        Execute(db, tx, "INSERT INTO jobs(queue,name,payload,state,created_at,updated_at) VALUES($q,$n,$p,'pending',$now,$now)",
            ("$q", queue), ("$n", name), ("$p", payload), ("$now", Now));
        var id = Scalar(db, tx, "SELECT last_insert_rowid()");
        try
        {
            foreach (var path in outputs ?? [])
                Execute(db, tx, "INSERT INTO output_reservations(path,job_id) VALUES($path,$id)", ("$path", path), ("$id", id));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        { throw new CliException("输出文件已被另一个未完成或结果待确认任务占用；请换一个输出位置。未入队、未发送请求。"); }
        Event(db, tx, id, "submitted", "任务已持久入队。");
        tx.Commit(); return id;
    }

    public ClaimedJob? TryClaim(QueueDefinition policy, long now)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT OR IGNORE INTO queues(id) VALUES($q)", ("$q", policy.Id));
        if (!policy.Enabled || Scalar(db, tx, "SELECT paused FROM control WHERE id=1") != 0 ||
            Scalar(db, tx, "SELECT COUNT(*) FROM queues WHERE id=$q AND (paused=1 OR next_at>$now)", ("$q", policy.Id), ("$now", now)) != 0 ||
            Scalar(db, tx, "SELECT COUNT(*) FROM jobs WHERE queue=$q AND state='running'", ("$q", policy.Id)) >= policy.MaxConcurrency ||
            Scalar(db, tx, "SELECT COUNT(*) FROM attempts WHERE queue=$q AND started_at>$since", ("$q", policy.Id), ("$since", now - 60000)) >= policy.RequestsPerMinute)
            return null;
        // Retries stay ahead of new work, but never bypass group cooldown or rate limits.
        using var cmd = Command(db, tx, """
            SELECT id,attempts,payload FROM jobs WHERE queue=$q AND state IN ('pending','retry_wait') AND next_at<=$now
            ORDER BY CASE state WHEN 'retry_wait' THEN 0 ELSE 1 END,id LIMIT 1
            """, ("$q", policy.Id), ("$now", now));
        ClaimedJob job;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            job = new(reader.GetInt64(0), policy.Id, reader.GetInt32(1) + 1, (byte[])reader[2]);
        }
        Execute(db, tx, "UPDATE jobs SET state='running',attempts=$a,updated_at=$now WHERE id=$id",
            ("$a", job.Attempt), ("$now", now), ("$id", job.Id));
        Execute(db, tx, "INSERT INTO attempts(job_id,number,queue,started_at) VALUES($id,$a,$q,$now)",
            ("$id", job.Id), ("$a", job.Attempt), ("$q", policy.Id), ("$now", now));
        var spacing = (long)Math.Ceiling(60000d / policy.RequestsPerMinute);
        Execute(db, tx, "UPDATE queues SET next_at=$next WHERE id=$q", ("$next", now + spacing), ("$q", policy.Id));
        Event(db, tx, job.Id, "started", $"开始第 {job.Attempt} 次尝试。");
        tx.Commit(); return job;
    }

    public void Complete(ClaimedJob job, QueueDefinition policy, CliReport report, int exit, long now)
    {
        var rateLimited = report.HttpStatus == 429 && report.ApiErrorCode is not ("insufficient_quota" or "billing_hard_limit_reached");
        var retry = rateLimited && job.Attempt <= policy.RetryCount;
        var delay = Math.Max(policy.RetryDelaySeconds, report.RetryAfterSeconds);
        // Never overflow a persisted timestamp on untrusted response headers; fail rather than retry too soon.
        if (!double.IsFinite(delay) || delay > TimeSpan.FromDays(365).TotalSeconds)
        { retry = false; delay = TimeSpan.FromDays(365).TotalSeconds; report.Error = "服务端等待时间过长；请人工检查限额。"; }
        var next = rateLimited ? now + (long)Math.Ceiling(delay * 1000) : 0;
        var state = exit == 0 ? JobState.Succeeded : retry ? JobState.Retry :
            report.Stage == "request" && report.HttpStatus is null or >= 200 and < 300 ? JobState.Unknown : JobState.Failed;
        if (retry) report.Error = $"HTTP 429：等待 {Math.Ceiling(delay)} 秒后重试（已使用 {job.Attempt - 1}/{policy.RetryCount} 次额外重试）。";
        else if (rateLimited && report.Error is null or "HTTP 429：请求失败。") report.Error = "HTTP 429：重试次数已耗尽。";
        using var db = Open(); using var tx = db.BeginTransaction();
        if (rateLimited)
            Execute(db, tx, "UPDATE queues SET next_at=MAX(next_at,$next) WHERE id=$q", ("$next", next), ("$q", job.Queue));
        Execute(db, tx, """
            UPDATE jobs SET state=$state,updated_at=$now,next_at=$next,http_status=$http,error=$error,result=$result
            WHERE id=$id AND state='running'
            """, ("$state", state), ("$now", now), ("$next", next), ("$http", report.HttpStatus),
            ("$error", exit == 0 ? null : report.Error), ("$result", report.Serialize(exit)), ("$id", job.Id));
        Execute(db, tx, """
            UPDATE attempts SET finished_at=$now,http_status=$http,request_id=$request,stage=$stage,error=$error
            WHERE job_id=$id AND number=$a
            """, ("$now", now), ("$http", report.HttpStatus), ("$request", report.RequestId), ("$stage", report.Stage),
            ("$error", report.Error), ("$id", job.Id), ("$a", job.Attempt));
        Event(db, tx, job.Id, state, exit == 0 ? "图片已保存。" : report.Error ?? "任务执行失败。");
        if (state is JobState.Succeeded or JobState.Failed)
            Execute(db, tx, "DELETE FROM output_reservations WHERE job_id=$id", ("$id", job.Id));
        tx.Commit();
    }

    // Call only after obtaining the exclusive worker lease. A previous process may have sent these jobs.
    public void RecoverInterrupted()
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        Execute(db, tx, """
            INSERT INTO events(time,job_id,kind,message)
            SELECT $now,id,'unknown','执行器中断，结果待确认；未自动重新生图。' FROM jobs WHERE state='running';
            UPDATE attempts SET finished_at=$now,stage='interrupted',error='执行器中断，结果待确认。'
                WHERE finished_at IS NULL;
            UPDATE jobs SET state='unknown',updated_at=$now,error='执行器中断，结果待确认；请核对输出和服务端记录。'
                WHERE state='running';
            """, ("$now", Now));
        tx.Commit();
    }

    public bool HasWork()
    { using var db = Open(); return Scalar(db, null, "SELECT COUNT(*) FROM jobs WHERE state IN ('pending','running','retry_wait')") > 0; }
    public List<string> ActiveQueues()
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT DISTINCT queue FROM jobs WHERE state IN ('pending','running','retry_wait')");
        using var reader = cmd.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    public void Pause(string? queue, bool paused)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (queue is null) Execute(db, tx, "UPDATE control SET paused=$p WHERE id=1", ("$p", paused ? 1 : 0));
        else
        {
            Execute(db, tx, "INSERT OR IGNORE INTO queues(id) VALUES($q)", ("$q", queue));
            Execute(db, tx, "UPDATE queues SET paused=$p WHERE id=$q", ("$p", paused ? 1 : 0), ("$q", queue));
        }
        Event(db, tx, null, paused ? "paused" : "resumed", queue is null ? "全部队列" : "队列 " + queue);
        tx.Commit();
    }

    public int Cancel(long? id = null, string? queue = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        const string where = "state IN ('pending','retry_wait') AND ($id IS NULL OR id=$id) AND ($q IS NULL OR queue=$q)";
        Execute(db, tx, "INSERT INTO events(time,job_id,kind,message) SELECT $now,id,'cancelled','用户取消未执行任务。' FROM jobs WHERE " + where,
            ("$now", Now), ("$id", id), ("$q", queue));
        var count = Execute(db, tx, "UPDATE jobs SET state='cancelled',updated_at=$now,error='用户取消。' WHERE " + where,
            ("$now", Now), ("$id", id), ("$q", queue));
        Execute(db, tx, "DELETE FROM output_reservations WHERE job_id IN (SELECT id FROM jobs WHERE state='cancelled')");
        tx.Commit(); return count;
    }

    private static QueueJob ReadJob(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt32(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : ParseResult(r.GetString(10)));
    private static JsonElement ParseResult(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private const string JobColumns = "id,queue,name,state,attempts,created_at,updated_at,next_at,http_status,error,result";
    public QueueJob? Get(long id)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT " + JobColumns + " FROM jobs WHERE id=$id", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadJob(r) : null;
    }
    public List<QueueJob> List(string? queue = null, string? state = null, int page = 1)
    {
        using var db = Open(); using var cmd = Command(db, null, "SELECT " + JobColumns + """
             FROM jobs WHERE ($q IS NULL OR queue=$q) AND ($s IS NULL OR state=$s)
             ORDER BY CASE WHEN state IN ('pending','running','retry_wait') THEN 0 ELSE 1 END,id DESC LIMIT 20 OFFSET $offset
            """, ("$q", queue), ("$s", state), ("$offset", (page - 1L) * 20));
        using var r = cmd.ExecuteReader(); var result = new List<QueueJob>();
        while (r.Read()) result.Add(ReadJob(r)); return result;
    }
    public List<QueueAttempt> Attempts(long id)
    {
        using var db = Open(); using var cmd = Command(db, null, """
            SELECT job_id,number,started_at,finished_at,http_status,request_id,stage,error FROM attempts WHERE job_id=$id ORDER BY number
            """, ("$id", id));
        using var r = cmd.ExecuteReader(); var result = new List<QueueAttempt>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3),
            r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return result;
    }
    public QueueSnapshot Snapshot(QueuePaths paths, QueueSettings settings, string? queue = null, string? state = null, int page = 1)
    {
        using var db = Open(); var now = Now;
        var overviews = new List<QueueOverview>();
        foreach (var policy in settings.Queues)
        {
            var counts = new Dictionary<string, int>();
            using (var cmd = Command(db, null, "SELECT state,COUNT(*) FROM jobs WHERE queue=$q GROUP BY state", ("$q", policy.Id)))
            using (var r = cmd.ExecuteReader()) while (r.Read()) counts[r.GetString(0)] = r.GetInt32(1);
            var paused = Scalar(db, null, "SELECT COALESCE(MAX(paused),0) FROM queues WHERE id=$q", ("$q", policy.Id)) != 0;
            var next = Scalar(db, null, "SELECT COALESCE(MAX(next_at),0) FROM queues WHERE id=$q", ("$q", policy.Id));
            var sent = (int)Scalar(db, null, "SELECT COUNT(*) FROM attempts WHERE queue=$q AND started_at>$since", ("$q", policy.Id), ("$since", now - 60000));
            overviews.Add(new(policy.Id, policy.RequestsPerMinute, policy.MaxConcurrency, policy.RetryCount,
                policy.RetryDelaySeconds, policy.Enabled, paused, next, sent, counts.GetValueOrDefault(JobState.Pending),
                counts.GetValueOrDefault(JobState.Running), counts.GetValueOrDefault(JobState.Retry), counts.GetValueOrDefault(JobState.Succeeded),
                counts.GetValueOrDefault(JobState.Failed), counts.GetValueOrDefault(JobState.Unknown), counts.GetValueOrDefault(JobState.Cancelled)));
        }
        return new(paths.WorkerRunning(), Scalar(db, null, "SELECT paused FROM control WHERE id=1") != 0,
            paths.SettingsFile, overviews, List(queue, state, page));
    }
}