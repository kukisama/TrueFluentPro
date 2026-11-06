using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services;

public readonly record struct VoiceImeUsageStatus(bool HistoryEnabled, bool Available, bool Updating = false);

/// <summary>
/// 系统 IME 专用。统计识别产出（定稿或调用者显式保留的末句），不代表 TSF 插入成功；上游负责同一厂商结果去重。
/// 一个 owner 持有一个实例；所有 SQLite I/O 在有界串行后台队列，失败熔断但不结束语音。
/// </summary>
public sealed class VoiceImeUsageService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly string _ownerScope;
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _worker;
    private bool _enabled, _available, _updating, _faulted, _disposed;
    private long _privacyVersion, _settingRequest;
    private Round? _round;
    public event Action<VoiceImeUsageStatus>? StatusChanged;
    public VoiceImeUsageStatus Status { get { lock (_gate) return new(_enabled, _available, _updating); } }

    // 构造不访问 DB；生产工厂也在 worker 上执行。scope 是 owner 的桌面会话，不是目标应用/窗口。
    public VoiceImeUsageService(Func<ISqliteDbService> database, string ownerScope, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _ownerScope = ownerScope;
        _worker = Task.Run(() => WriteLoopAsync(database));
    }

    public Guid BeginSession()
    {
        lock (_gate)
        {
            _round?.ClearPending();
            var round = new Round(Guid.NewGuid(), Stamp());
            _round = round;
            Save(round, "connecting", null);
            return round.Id;
        }
    }

    public void RecordingStarted(Guid id)
    {
        lock (_gate)
        {
            if (_round is not { } r || r.Id != id || r.Started != null) return;
            r.Started = Stamp(); r.StartTimestamp = _clock.GetTimestamp();
            Save(r, "recording", null);
        }
    }

    public void Recognized(Guid id, string text, bool final)
    {
        lock (_gate)
        {
            if (_faulted || _disposed || _round is not { } r || r.Id != id) return;
            if (text.Length > 65536) { Fault(); return; } // final/partial 同样有界，不把正文带入错误信息。
            // 句首版本一直保留到本句结束；pending/on/off/on 后的新快照也不能补存旧句。
            r.SentencePrivacyVersion ??= _privacyVersion;
            var original = _enabled && _available && r.SentencePrivacyVersion == _privacyVersion ? text : null;
            var characters = CountCharacters(text);
            if (!final)
            {
                r.PendingCharacters = characters;
                r.HasPending = !string.IsNullOrWhiteSpace(text);
                r.PendingOriginal = original;
                return;
            }
            FinalizeSentence(r, characters, original);
        }
    }

    public void Checkpoint(Guid id)
    {
        lock (_gate)
        {
            if (_round is { } r && r.Id == id && r.Started != null) Save(r, "recording", null);
        }
    }

    /// <summary>
    /// 立即结束统计会话，不等待云服务或 SQLite。仅 ASR 手动停止且保留末句时由调用者传 true；
    /// 翻译及其他结束路径保持默认 false，不自动定稿尚未输入的 partial。
    /// </summary>
    public void EndSession(Guid id, string reason, bool finalizePending = false)
    {
        if (reason is not ("manual_stop" or "inactivity_timeout" or "other_ime" or "failed" or "owner_exit"))
            throw new ArgumentOutOfRangeException(nameof(reason));
        lock (_gate)
        {
            if (_round is not { } r || r.Id != id) return;
            _round = null;
            if (finalizePending && r.HasPending)
            {
                var original = _enabled && _available && r.SentencePrivacyVersion == _privacyVersion
                    ? r.PendingOriginal : null;
                FinalizeSentence(r, r.PendingCharacters, original);
            }
            r.ClearPending();
            Save(r, "ended", reason);
        }
    }

    /// <summary>调用者已完成用户确认；立即撤销捕捉，只有 SQLite 成功后才允许开启。</summary>
    public Task<bool> SetHistoryAsync(bool enabled)
    {
        lock (_gate)
        {
            if (_faulted || _disposed)
            {
                _enabled = _available = _updating = false;
                Publish();
                return Task.FromResult(false);
            }
            _enabled = false; _available = false; _updating = true; AdvancePrivacyVersion();
            var request = ++_settingRequest;
            Publish();
            return Enqueue(store =>
            {
                store.SetHistory(enabled);
                lock (_gate)
                {
                    if (!_faulted && !_disposed && request == _settingRequest)
                    {
                        _enabled = enabled; _available = true; _updating = false; AdvancePrivacyVersion();
                        Publish();
                    }
                }
            });
        }
    }

    // 明确的队列屏障，fixture 不靠固定等待时间猜测 SQLite 是否已经落盘。
    public Task<bool> FlushAsync() { lock (_gate) return Enqueue(_ => { }); }

    public static int CountCharacters(string text)
    {
        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            foreach (var rune in elements.GetTextElement().EnumerateRunes())
            {
                var category = Rune.GetUnicodeCategory(rune);
                // 一个可见字素只计一次，组合音标/ZWJ/变体选择符不额外计数。
                if (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune) || category is
                    UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
                    UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                    continue;
                count++; break;
            }
        }
        return count;
    }

    private UsageTime Stamp()
    {
        var utc = _clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(utc, _clock.LocalTimeZone);
        return new(utc.ToString("O", CultureInfo.InvariantCulture), local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            (int)local.Offset.TotalMinutes);
    }

    private void Save(Round r, string state, string? reason)
    {
        var snapshot = new Session(r.Id, _ownerScope, r.Created, r.Started, Stamp(),
            r.Started == null ? 0 : Math.Max(0, (long)_clock.GetElapsedTime(r.StartTimestamp).TotalMilliseconds),
            state, reason, ++r.Revision);
        Enqueue(store => store.WriteSession(snapshot));
    }

    private void FinalizeSentence(Round r, int characters, string? original)
    {
        var utterance = new Utterance(r.Id, ++r.Sequence, Stamp(), characters, original);
        Enqueue(store => store.WriteUtterance(utterance));
        r.ClearPending();
    }

    private void AdvancePrivacyVersion()
    {
        ++_privacyVersion;
        if (_round is { } r) r.PendingOriginal = null;
    }

    private Task<bool> Enqueue(Action<Store> action)
    {
        if (_faulted || _disposed) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new(action, completion)))
        {
            Fault(); completion.TrySetResult(false);
        }
        return completion.Task;
    }

    private void Fault()
    {
        lock (_gate)
        {
            if (_faulted) return;
            _faulted = true; _enabled = _available = _updating = false; AdvancePrivacyVersion();
            Publish(); // 永不输出 SQL/异常/正文；available=false 是明确的可用性信号。
        }
    }

    private void Publish()
    {
        try { StatusChanged?.Invoke(new(_enabled, _available, _updating)); } catch { }
    }

    private async Task WriteLoopAsync(Func<ISqliteDbService> factory)
    {
        ISqliteDbService? database = null;
        Store? store = null;
        try
        {
            database = factory();
            store = new Store(database);
            var history = store.Initialize(_ownerScope);
            lock (_gate)
            {
                if (!_faulted && !_disposed && _settingRequest == 0)
                {
                    _enabled = history; _available = true; AdvancePrivacyVersion();
                    Publish();
                }
            }
        }
        catch { Fault(); }
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var success = false;
                try
                {
                    bool healthy;
                    lock (_gate) healthy = !_faulted;
                    if (healthy && store != null) { work.Action(store); success = true; }
                }
                catch { Fault(); }
                finally { work.Completion.TrySetResult(success); }
            }
        }
        finally { database?.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                if (_round is { } r) EndSession(r.Id, "owner_exit");
                _disposed = true; _enabled = _available = _updating = false; AdvancePrivacyVersion();
                Publish();
                _queue.Writer.TryComplete();
            }
        }
        await _worker.ConfigureAwait(false);
    }

    private sealed class Round(Guid id, UsageTime created)
    {
        internal readonly Guid Id = id;
        internal readonly UsageTime Created = created;
        internal UsageTime? Started;
        internal long StartTimestamp, Sequence, Revision;
        internal long? SentencePrivacyVersion;
        internal int PendingCharacters;
        internal bool HasPending;
        internal string? PendingOriginal;
        internal void ClearPending()
        {
            PendingCharacters = 0; HasPending = false; PendingOriginal = null; SentencePrivacyVersion = null;
        }
    }
    private sealed record Work(Action<Store> Action, TaskCompletionSource<bool> Completion);
    internal sealed record UsageTime(string Utc, string LocalDate, int OffsetMinutes);
    internal sealed record Session(Guid Id, string Scope, UsageTime Created, UsageTime? Started, UsageTime Observed,
        long RecordingMs, string State, string? Reason, long Revision);
    internal sealed record Utterance(Guid SessionId, long Sequence, UsageTime Time, int Characters, string? Original);

    // records 及 SQL 写入集中在一处，重试复用同一不可变 session/sequence，不按正文去重。
    internal sealed class Store(ISqliteDbService database)
    {
        private const string HistoryKey = "voice_ime.history_enabled";
        internal bool Initialize(string scope)
        {
            using var connection = database.CreateConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var recover = connection.CreateCommand();
            recover.Transaction = transaction;
            recover.CommandText = "UPDATE voice_ime_sessions SET state='interrupted', stop_reason='interrupted' " +
                "WHERE owner_scope=@scope AND state IN ('connecting','recording');";
            recover.Parameters.AddWithValue("@scope", scope);
            recover.ExecuteNonQuery();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT value FROM _meta WHERE key=@key;";
            read.Parameters.AddWithValue("@key", HistoryKey);
            var value = read.ExecuteScalar();
            var enabled = value switch { null => false, "0" => false, "1" => true, _ => throw new InvalidDataException() };
            transaction.Commit();
            return enabled; // 未结束会话只保留最后检查点时长，绝不把重启时间当结束时间。
        }

        internal void SetHistory(bool enabled) => database.SetMeta(HistoryKey, enabled ? "1" : "0");

        internal void WriteSession(Session value)
        {
            using var connection = database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO voice_ime_sessions(session_id, owner_scope, created_utc, created_local_date, created_offset_minutes,
 recording_started_utc, checkpoint_utc, checkpoint_local_date, checkpoint_offset_minutes, ended_utc,
 recording_ms, state, stop_reason, revision)
VALUES(@id,@scope,@created,@date,@offset,@started,@observed,@observedDate,@observedOffset,@ended,@ms,@state,@reason,@revision)
ON CONFLICT(session_id) DO UPDATE SET recording_started_utc=excluded.recording_started_utc,
 checkpoint_utc=excluded.checkpoint_utc, checkpoint_local_date=excluded.checkpoint_local_date,
 checkpoint_offset_minutes=excluded.checkpoint_offset_minutes, ended_utc=excluded.ended_utc,
 recording_ms=excluded.recording_ms, state=excluded.state, stop_reason=excluded.stop_reason, revision=excluded.revision
WHERE excluded.revision > voice_ime_sessions.revision AND voice_ime_sessions.state NOT IN ('ended','interrupted');";
            command.Parameters.AddWithValue("@id", value.Id.ToString("D"));
            command.Parameters.AddWithValue("@scope", value.Scope);
            command.Parameters.AddWithValue("@created", value.Created.Utc);
            command.Parameters.AddWithValue("@date", value.Created.LocalDate);
            command.Parameters.AddWithValue("@offset", value.Created.OffsetMinutes);
            command.Parameters.AddWithValue("@started", (object?)value.Started?.Utc ?? DBNull.Value);
            command.Parameters.AddWithValue("@observed", value.Observed.Utc);
            command.Parameters.AddWithValue("@observedDate", value.Observed.LocalDate);
            command.Parameters.AddWithValue("@observedOffset", value.Observed.OffsetMinutes);
            command.Parameters.AddWithValue("@ended", value.State == "ended" ? value.Observed.Utc : DBNull.Value);
            command.Parameters.AddWithValue("@ms", value.RecordingMs);
            command.Parameters.AddWithValue("@state", value.State);
            command.Parameters.AddWithValue("@reason", (object?)value.Reason ?? DBNull.Value);
            command.Parameters.AddWithValue("@revision", value.Revision);
            command.ExecuteNonQuery();
        }

        internal void WriteUtterance(Utterance value)
        {
            using var connection = database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
INSERT INTO voice_ime_utterances(session_id, sequence_no, finalized_utc, local_date, utc_offset_minutes, character_count, original_text)
VALUES(@id,@sequence,@utc,@date,@offset,@count,@text) ON CONFLICT(session_id,sequence_no) DO NOTHING;";
            command.Parameters.AddWithValue("@id", value.SessionId.ToString("D"));
            command.Parameters.AddWithValue("@sequence", value.Sequence);
            command.Parameters.AddWithValue("@utc", value.Time.Utc);
            command.Parameters.AddWithValue("@date", value.Time.LocalDate);
            command.Parameters.AddWithValue("@offset", value.Time.OffsetMinutes);
            command.Parameters.AddWithValue("@count", value.Characters);
            command.Parameters.AddWithValue("@text", (object?)value.Original ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
}