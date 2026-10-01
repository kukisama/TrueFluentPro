using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GptImageCli;
using Microsoft.Data.Sqlite;

internal static class QueueTests
{
    private const long Start = 1_800_000_000_000;

    // Call with the runner's project-local artifact directory. No parser, real config or network.
    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: QueueTests requires Windows (winsqlite3 / current-user DPAPI).");
            return;
        }

        root = Path.GetFullPath(root);
        source = Path.GetFullPath(source);
        if (!Inside(root, source)) throw new ArgumentException("QueueTests 的参考图必须位于测试 root 内。", nameof(source));
        var suite = Path.Combine(root, "queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(suite);
        var key = "sk_queue_tests_" + Guid.NewGuid().ToString("N");

        Settings(new QueuePaths(Path.Combine(suite, "settings")), check);
        Dispatch(new QueuePaths(Path.Combine(suite, "dispatch")), key, check);
        RateWindow(new QueuePaths(Path.Combine(suite, "rpm")), key, check);
        Retries(new QueuePaths(Path.Combine(suite, "retry")), key, check);
        AttemptEvidence(new QueuePaths(Path.Combine(suite, "evidence")), key, check);
        OverlappingCooldown(new QueuePaths(Path.Combine(suite, "overlapping-cooldown")), key, check);
        Isolation(new QueuePaths(Path.Combine(suite, "isolation")), key, check);
        LegacyAttempts(new QueuePaths(Path.Combine(suite, "legacy")), key, check);
        Quota(new QueuePaths(Path.Combine(suite, "quota")), key, check);
        RecoveryAndCancel(new QueuePaths(Path.Combine(suite, "recovery")), key, check);
        OutputReservations(new QueuePaths(Path.Combine(suite, "output-reservations")), key, check);
        Snapshot(new QueuePaths(Path.Combine(suite, "snapshot")), source, png, key, check);
        await LegacyWorkerAsync(new QueuePaths(Path.Combine(suite, "legacy-worker")), check);
        await WorkerAsync(new QueuePaths(Path.Combine(suite, "worker")), source, png, key, check);
        await FailureOutputsAsync(new QueuePaths(Path.Combine(suite, "failure-output")), source, png, key, check);
        check(File.Exists(source) && File.ReadAllBytes(source).SequenceEqual(png), "queue: caller's reference image remains unchanged");
    }

    private static void Settings(QueuePaths paths, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        check(settings.Version == 1 && settings.Queues.Count == 3, "queue: default settings version and three groups");
        foreach (var (model, id, rpm, concurrency) in new[]
        {
            ("gpt-image-2", "image2", 9, 4),
            ("gpt-image-2.5-flare", "flare", 2, 1),
            ("gpt-image-2.5-sunburst", "sunburst", 2, 1)
        })
        {
            var policy = settings.ForModel(model);
            check(policy.Id == id && policy.RequestsPerMinute == rpm && policy.MaxConcurrency == concurrency &&
                policy.RetryCount == 2 && policy.RetryDelaySeconds == 61 && policy.Enabled,
                "queue: default policy " + id);
        }

        settings.ForModel("gpt-image-2").Models.Add("my-image2-deployment");
        settings.Queues.Add(new QueueDefinition
        {
            Id = "custom", Models = ["my-custom-model"], RequestsPerMinute = 17,
            MaxConcurrency = 3, RetryCount = 1, RetryDelaySeconds = 7, Enabled = true
        });
        paths.SaveSettings(settings);
        var reloaded = paths.LoadSettings();
        var custom = reloaded.ForModel("MY-CUSTOM-MODEL");
        check(reloaded.Queues.Count == 4 && reloaded.ForModel("my-image2-deployment").Id == "image2" &&
            custom.Id == "custom" && custom.RequestsPerMinute == 17 && custom.MaxConcurrency == 3 &&
            custom.RetryCount == 1 && custom.RetryDelaySeconds == 7 && custom.Enabled,
            "queue: custom model/group saved and hot-reloaded from the same settings path");

        var saved = File.ReadAllText(paths.SettingsFile);
        foreach (var (name, invalidate) in new (string, Action<QueueSettings>)[]
        {
            ("duplicate model across groups", s => s.Queues[1].Models.Add("GPT-IMAGE-2")),
            ("zero RPM", s => s.Queues[0].RequestsPerMinute = 0),
            ("excess RPM", s => s.Queues[0].RequestsPerMinute = 10001),
            ("zero concurrency", s => s.Queues[0].MaxConcurrency = 0),
            ("excess concurrency", s => s.Queues[0].MaxConcurrency = 65),
            ("negative retries", s => s.Queues[0].RetryCount = -1),
            ("excess retries", s => s.Queues[0].RetryCount = 101),
            ("zero retry delay", s => s.Queues[0].RetryDelaySeconds = 0),
            ("excess retry delay", s => s.Queues[0].RetryDelaySeconds = 86401)
        })
        {
            var invalid = paths.LoadSettings();
            invalidate(invalid);
            var rejected = false;
            try { paths.SaveSettings(invalid); }
            catch (CliException) { rejected = true; }
            check(rejected, "queue: settings reject " + name);
        }
        check(File.ReadAllText(paths.SettingsFile) == saved && paths.LoadSettings().ForModel("my-custom-model").Id == "custom",
            "queue: rejected settings leave the last valid configuration intact");
    }

    private static void Dispatch(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        settings.ForModel("gpt-image-2").Models.Add("image2-alias");
        paths.SaveSettings(settings);
        settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths); // QueueStore owns no persistent connection and is not IDisposable.
        var ids = Enumerable.Range(0, 5).Select(i => Submit(paths, store, settings, key,
            i % 2 == 0 ? "gpt-image-2" : "image2-alias", "parallel-" + i)).ToArray();
        var flareId = Submit(paths, store, settings, key, "gpt-image-2.5-flare", "independent-flare");
        var spacing = (long)Math.Ceiling(60000d / policy.RequestsPerMinute);
        var running = new List<ClaimedJob>();
        for (var i = 0; i < 4; i++)
        {
            if (i > 0)
                check(store.TryClaim(policy, Start + i * spacing - 1) is null,
                    "queue: shared model group blocks dispatch one millisecond before spacing " + i);
            var job = Claim(store, policy, Start + i * spacing);
            check(job.Id == ids[i] && job.Attempt == 1, "queue: shared model group dispatches at spacing " + i);
            running.Add(job);
        }
        var fifthTime = Start + 4 * spacing;
        check(ids.Take(4).All(id => store.Get(id)?.State == JobState.Running) &&
            store.TryClaim(policy, fifthTime) is null && store.Get(ids[4])?.State == JobState.Pending &&
            store.Attempts(ids[4]).Count == 0, "queue: four remain running; fifth blocked without consuming an attempt");
        check(Claim(store, settings.ForModel("gpt-image-2.5-flare"), fifthTime).Id == flareId,
            "queue: flare dispatches independently of saturated image2 concurrency");
        store.Complete(running[0], policy, Success(), 0, fifthTime);
        check(Claim(store, policy, fifthTime).Id == ids[4], "queue: freeing one slot admits fifth at the same virtual millisecond");
    }

    private static void RateWindow(QueuePaths paths, string key, Action<bool, string> check)
    {
        var store = new QueueStore(paths);
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var ids = Enumerable.Range(0, 10).Select(i => Submit(paths, store, settings, key, "gpt-image-2", "rpm-" + i)).ToArray();
        // At a steady 9 RPM, spacing also blocks the tenth request. Hot-lowering a quota
        // isolates the rolling-window guard from spacing and concurrency, without sleeping.
        policy.RequestsPerMinute = 10000;
        paths.SaveSettings(settings);
        policy = paths.LoadSettings().ForModel("gpt-image-2");
        for (var i = 0; i < 9; i++)
        {
            var job = Claim(store, policy, Start + i * 6);
            store.Complete(job, policy, Success(), 0, Start + i * 6);
        }
        settings.ForModel("gpt-image-2").RequestsPerMinute = 9;
        paths.SaveSettings(settings);
        policy = paths.LoadSettings().ForModel("gpt-image-2");
        check(store.TryClaim(policy, Start + 54) is null && store.TryClaim(policy, Start + 59999) is null &&
            store.Attempts(ids[9]).Count == 0, "queue: hot-reloaded RPM blocks tenth despite available slot and elapsed spacing");
        var flareId = Submit(paths, store, settings, key, "gpt-image-2.5-flare", "rpm-independent");
        check(Claim(store, settings.ForModel("gpt-image-2.5-flare"), Start + 54).Id == flareId,
            "queue: image2 minute window does not consume flare quota");
        check(Claim(store, policy, Start + 60000).Id == ids[9], "queue: minute window expires at exact virtual boundary");
    }

    private static void Retries(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        var id = Submit(paths, store, settings, key, "gpt-image-2", "limited");
        var following = Submit(paths, store, settings, key, "gpt-image-2", "following");
        var flareId = Submit(paths, store, settings, key, "gpt-image-2.5-flare", "during-cooldown");
        var now = Start;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = Claim(store, policy, now);
            check(job.Id == id && job.Attempt == attempt, "queue: due retry precedes pending work, attempt " + attempt);
            // The first short Retry-After must not shorten the configured 61-second delay.
            var report = Limited(key, attempt == 1 ? 1 : 61, requestId: "queue-attempt-" + attempt);
            check(report.HttpStatus == 429 && report.ApiErrorCode == "rate_limit_exceeded" &&
                report.RetryAfterSeconds == (attempt == 1 ? 1 : 61) && !report.Serialize(1).Contains(key),
                "queue: real header/body capture preserves safe error code, not key, attempt " + attempt);
            var finished = now + 10;
            store.Complete(job, policy, report, 1, finished);
            now = finished + 61000;
            var saved = store.Get(id)!;
            check(saved.State == (attempt < 3 ? JobState.Retry : JobState.Failed) && saved.Attempts == attempt &&
                saved.NextAttemptAt == now && saved.HttpStatus == 429 &&
                store.Snapshot(paths, settings).Queues.Single(q => q.Id == policy.Id).NextDispatchAt == now,
                "queue: 61-second group cooldown persisted, attempt " + attempt);
            check(store.TryClaim(policy, now - 1) is null && store.Get(following)?.Attempts == 0,
                "queue: neither retry nor following task bypasses cooldown, attempt " + attempt);
            if (attempt == 1)
            {
                var flare = settings.ForModel("gpt-image-2.5-flare");
                var other = Claim(store, flare, finished + 1);
                check(other.Id == flareId, "queue: flare remains dispatchable during image2 retry cooldown");
                store.Complete(other, flare, Success(), 0, finished + 1);
            }
        }
        var history = store.Attempts(id);
        check(history.Count == 3 && history.Select(a => a.Number).SequenceEqual(new[] { 1, 2, 3 }) &&
            history.All(a => a.HttpStatus == 429 && a.FinishedAt.HasValue && a.Stage == "response" &&
                a.RequestId == "queue-attempt-" + a.Number) && store.Get(id)?.State == JobState.Failed,
            "queue: initial attempt plus two retries finally fails with complete HTTP attempt history");
        check(Claim(store, policy, now).Id == following && store.Get(id)?.Attempts == 3,
            "queue: following task starts only after final failure's cooldown; failed task is not retried");
    }

    private static void AttemptEvidence(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        var id = Submit(paths, store, settings, key, "gpt-image-2", "evidence");
        var first = Claim(store, policy, Start);
        var claimed = store.Attempts(id).Single();
        check(claimed.Configuration is { } configuration &&
            configuration.GetProperty("requestsPerMinute").GetInt32() == 9 &&
            configuration.GetProperty("maxConcurrency").GetInt32() == 4 &&
            configuration.GetProperty("retryCount").GetInt32() == 2 &&
            configuration.GetProperty("retryDelaySeconds").GetInt32() == 61 &&
            claimed.Dispatch is { Queue: "image2", Reason: "queue_limits_satisfied", RunningBefore: 0,
                SentLastMinute: 0, SpacingMilliseconds: 6667 } &&
            claimed.Result is null && claimed.Retry is null,
            "queue: claim durably records policy and admission counters before HTTP completes");
        var failure = Limited(key, 90, requestId: "first-attempt-evidence");
        var originalError = failure.Error;
        using var original = JsonDocument.Parse(failure.Serialize(1));
        store.Complete(first, policy, failure, 1, Start + 10);
        var failedAttempt = store.Attempts(id).Single();
        check(failedAttempt.Error == originalError && failedAttempt.Result is { } failedResult &&
            SameReport(failedResult, original.RootElement) &&
            failedResult.GetProperty("http_status").GetInt32() == 429 &&
            failedResult.GetProperty("api_error").GetProperty("code").GetString() == "rate_limit_exceeded" &&
            !failedResult.GetRawText().Contains(key) && failedAttempt.RequestId == "first-attempt-evidence" &&
            store.Get(id)!.Error != originalError,
            "queue: attempt retains original sanitized report/error, not the job's generic retry summary");
        check(failedAttempt.Retry is { WillRetry: true, Reason: "http_429", RetryCount: 2, RetriesUsed: 0,
                ConfiguredDelaySeconds: 61, ServerDelaySeconds: 90, AppliedDelaySeconds: 90,
                DelaySource: "server", CooldownScope: "queue", Queue: "image2" } decision &&
            !string.IsNullOrEmpty(decision.ServerDelaySource) &&
            decision.CooldownUntil == Start + 90010 && decision.NextAttemptAt == Start + 90010,
            "queue: per-attempt retry evidence explains server-selected delay and queue deadline");

        policy.RequestsPerMinute = 17;
        policy.MaxConcurrency = 3;
        policy.RetryCount = 4;
        policy.RetryDelaySeconds = 7;
        paths.SaveSettings(settings);
        var updated = paths.LoadSettings().ForModel("gpt-image-2");
        var second = Claim(store, updated, Start + 90010);
        var success = Success();
        using var successReport = JsonDocument.Parse(success.Serialize(0));
        store.Complete(second, updated, success, 0, Start + 90020);
        var history = store.Attempts(id);
        var reopened = new QueueStore(new QueuePaths(paths.Root));
        var persisted = reopened.Attempts(id);
        check(reopened.Get(id) is { State: JobState.Succeeded, Attempts: 2, Error: null } &&
            persisted.Count == 2 && SameAttempts(history, persisted) &&
            persisted[0].Result is { } firstResult && JsonElement.DeepEquals(firstResult, failedAttempt.Result!.Value) &&
            persisted[1].Result is { } lastResult && SameReport(lastResult, successReport.RootElement) &&
            persisted[0].Error == originalError && persisted[0].HttpStatus == 429 && persisted[1].HttpStatus == 200 &&
            persisted[1].Retry is { WillRetry: false, Reason: "succeeded", CooldownScope: "none", AppliedDelaySeconds: 0 },
            "queue: full 429 and 200 attempt reports and decisions survive success and same-path reopen");
        check(persisted[0].Configuration is { } oldPolicy &&
            oldPolicy.GetProperty("requestsPerMinute").GetInt32() == 9 &&
            oldPolicy.GetProperty("maxConcurrency").GetInt32() == 4 &&
            oldPolicy.GetProperty("retryCount").GetInt32() == 2 &&
            oldPolicy.GetProperty("retryDelaySeconds").GetInt32() == 61 &&
            persisted[1].Configuration is { } newPolicy &&
            newPolicy.GetProperty("requestsPerMinute").GetInt32() == 17 &&
            newPolicy.GetProperty("maxConcurrency").GetInt32() == 3 &&
            newPolicy.GetProperty("retryCount").GetInt32() == 4 &&
            newPolicy.GetProperty("retryDelaySeconds").GetInt32() == 7 &&
            persisted[1].Dispatch is { RunningBefore: 0, SentLastMinute: 0, SpacingMilliseconds: 3530 },
            "queue: hot changes apply to the next claim without rewriting historical configuration");
    }

    private static void OverlappingCooldown(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        var firstId = Submit(paths, store, settings, key, "gpt-image-2", "long-cooldown");
        var secondId = Submit(paths, store, settings, key, "gpt-image-2", "short-cooldown");
        var first = Claim(store, policy, Start);
        var second = Claim(store, policy, Start + 6667);
        check(store.Attempts(secondId).Single().Dispatch is { RunningBefore: 1, SentLastMinute: 1 },
            "queue: overlapping claim records only prior running requests and sends");
        store.Complete(first, policy, Limited(key, 120), 1, Start + 7000);
        store.Complete(second, policy, Limited(key, 1), 1, Start + 8000);
        var earlier = store.Attempts(firstId).Single().Retry!;
        var later = store.Attempts(secondId).Single().Retry!;
        check(earlier.CooldownUntil == Start + 127000 &&
            later is { ConfiguredDelaySeconds: 61, ServerDelaySeconds: 1, AppliedDelaySeconds: 61,
                DelaySource: "configuration", CooldownScope: "queue" } &&
            later.NextAttemptAt == Start + 69000 && later.CooldownUntil == earlier.CooldownUntil &&
            store.Snapshot(paths, settings).Queues.Single(q => q.Id == policy.Id).NextDispatchAt == earlier.CooldownUntil,
            "queue: later short response records the effective longer overlapping group cooldown");
        var reopened = new QueueStore(new QueuePaths(paths.Root));
        check(reopened.TryClaim(policy, later.NextAttemptAt) is null &&
            reopened.TryClaim(policy, earlier.CooldownUntil - 1) is null &&
            Claim(reopened, policy, earlier.CooldownUntil).Id == firstId,
            "queue: overlapping cooldown survives reopen and expires only at its effective deadline");
    }

    private static void Isolation(QueuePaths paths, string key, Action<bool, string> check)
    {
        foreach (var blockedId in new[] { "image2", "flare", "sunburst" })
        foreach (var limit in new[] { "concurrency", "rpm", "cooldown" })
        {
            var casePaths = new QueuePaths(Path.Combine(paths.Root, blockedId + "-" + limit));
            var settings = casePaths.LoadSettings();
            var blocked = settings.Queues.Single(q => q.Id == blockedId);
            var store = new QueueStore(casePaths);
            var count = limit == "concurrency" ? blocked.MaxConcurrency : limit == "rpm" ? blocked.RequestsPerMinute : 1;
            var ids = Enumerable.Range(0, count + 1)
                .Select(i => Submit(casePaths, store, settings, key, blocked.Models[0], "blocked-" + i)).ToArray();
            var peers = settings.Queues.Where(q => q.Id != blockedId)
                .Select(q => (Policy: q, Id: Submit(casePaths, store, settings, key, q.Models[0], "peer-" + q.Id))).ToArray();
            var rpm = blocked.RequestsPerMinute;
            if (limit == "rpm") blocked.RequestsPerMinute = 10000;
            var spacing = (long)Math.Ceiling(60000d / blocked.RequestsPerMinute);
            for (var i = 0; i < count; i++)
            {
                var job = Claim(store, blocked, Start + i * spacing);
                if (limit == "rpm") store.Complete(job, blocked, Success(), 0, Start + i * spacing);
                if (limit == "cooldown") store.Complete(job, blocked, Limited(key, 120), 1, Start + 1);
            }
            if (limit == "rpm") blocked.RequestsPerMinute = rpm;
            var now = limit == "rpm" ? Start + 59999 : Start + count * spacing;
            check(store.TryClaim(blocked, now) is null && store.Attempts(ids[^1]).Count == 0,
                $"queue isolation: {blockedId} is blocked by its own {limit} without consuming attempts");
            foreach (var peer in peers)
            {
                var admitted = Claim(store, peer.Policy, now);
                check(admitted.Id == peer.Id &&
                    store.Attempts(peer.Id).Single().Dispatch is { RunningBefore: 0, SentLastMinute: 0 },
                    $"queue isolation: {blockedId} {limit} does not block {peer.Policy.Id} or consume its counters");
            }
            check(store.TryClaim(blocked, now) is null,
                $"queue isolation: dispatching both peers does not clear {blockedId} {limit}");
        }
    }

    private static void LegacyAttempts(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        var id = Submit(paths, store, settings, key, "gpt-image-2", "legacy");
        store.Complete(Claim(store, policy, Start), policy, Success(), 0, Start + 1);
        // Removing only the additive table recreates the existing version-1 schema.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabaseFile, Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE attempt_diagnostics; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        var reopened = new QueueStore(new QueuePaths(paths.Root));
        check(reopened.Get(id) is { State: JobState.Succeeded, Attempts: 1 } &&
            reopened.Attempts(id).Single() is { Number: 1, HttpStatus: 200, Stage: "save",
                Configuration: null, Dispatch: null, Result: null, Retry: null },
            "queue: old version-1 attempts remain readable with absent diagnostics, without fabricated evidence");
        var next = Submit(paths, reopened, settings, key, "gpt-image-2", "after-upgrade");
        check(Claim(reopened, policy, Start + 6667).Id == next &&
            reopened.Attempts(next).Single() is { Configuration: not null, Dispatch: not null },
            "queue: additive upgrade records diagnostics for new claims alongside legacy history");
    }

    private static void Quota(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        var id = Submit(paths, store, settings, key, "gpt-image-2", "no-quota");
        var following = Submit(paths, store, settings, key, "gpt-image-2", "after-quota");
        var job = Claim(store, policy, Start);
        store.Complete(job, policy, Limited(key, 61, "insufficient_quota"), 1, Start + 1);
        check(store.Get(id) is { State: JobState.Failed, Attempts: 1, NextAttemptAt: 0, HttpStatus: 429 },
            "queue: insufficient_quota is terminal, not retryable 429");
        check(Claim(store, policy, Start + (long)Math.Ceiling(60000d / policy.RequestsPerMinute)).Id == following,
            "queue: quota failure does not add retry cooldown to following work");
    }

    private static void RecoveryAndCancel(QueuePaths paths, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var flare = settings.ForModel("gpt-image-2.5-flare");
        var store = new QueueStore(paths);
        var interrupted = Submit(paths, store, settings, key, "gpt-image-2", "interrupted");
        var retryId = Submit(paths, store, settings, key, "gpt-image-2", "recover-retry");
        var pendingId = Submit(paths, store, settings, key, "gpt-image-2", "recover-pending");
        var completedId = Submit(paths, store, settings, key, "gpt-image-2.5-flare", "recover-success");

        store.Pause(null, true);
        check(store.TryClaim(policy, Start) is null && store.TryClaim(flare, Start) is null,
            "queue: global pause blocks all claims");
        store.Pause(null, false);
        store.Pause(policy.Id, true);
        check(store.TryClaim(policy, Start) is null, "queue: group pause blocks that group's claims");
        var other = Claim(store, flare, Start);
        check(other.Id == completedId, "queue: group pause does not pause flare");
        store.Complete(other, flare, Success(), 0, Start + 1);
        store.Pause(policy.Id, false);
        var running = Claim(store, policy, Start);
        var interruptedBefore = store.Attempts(interrupted).Single();
        check(running.Id == interrupted && store.Cancel(interrupted) == 0 && store.Get(interrupted)?.State == JobState.Running,
            "queue: resume permits claim; cancel refuses running work");
        var retry = Claim(store, policy, Start + 6667);
        store.Complete(retry, policy, Limited(key, 61), 1, Start + 6670);
        var before = store.Get(retryId)!;
        var attemptsBefore = store.Attempts(retryId);
        store.Pause(null, true);
        store.Pause(policy.Id, true);
        // Recovery must run under the same exclusive lease as the production worker.
        using (var lease = paths.TryLock("worker"))
        {
            check(lease is not null, "queue: recovery owns exclusive worker lease");
            store.RecoverInterrupted();
        }

        var reopenedPaths = new QueuePaths(paths.Root);
        var reopened = new QueueStore(reopenedPaths);
        var after = reopened.Get(retryId)!;
        var snapshot = reopened.Snapshot(reopenedPaths, reopenedPaths.LoadSettings());
        check(reopened.Get(interrupted) is { State: JobState.Unknown, Attempts: 1 } &&
            reopened.Get(pendingId) is { State: JobState.Pending, Attempts: 0 } &&
            reopened.Get(completedId) is { State: JobState.Succeeded, Attempts: 1 } &&
            after.State == JobState.Retry && after.Attempts == before.Attempts && after.NextAttemptAt == before.NextAttemptAt &&
            after.HttpStatus == before.HttpStatus && after.Result?.GetRawText() == before.Result?.GetRawText() &&
            SameAttempts(reopened.Attempts(retryId), attemptsBefore),
            "queue: same-path DB reopen preserves retry/deadline/pending/success; only running becomes unknown");
        check(reopened.Attempts(interrupted).Single() is { Stage: "interrupted", FinishedAt: not null } &&
            snapshot.Paused && snapshot.Queues.Single(q => q.Id == policy.Id).Paused &&
            snapshot.Queues.Single(q => q.Id == policy.Id).NextDispatchAt == before.NextAttemptAt,
            "queue: interrupted attempt closed; pause flags and group cooldown survive reopen");
        var interruptedAfter = reopened.Attempts(interrupted).Single();
        check(interruptedBefore.Configuration is { } claimedPolicy && interruptedAfter.Configuration is { } recoveredPolicy &&
            JsonElement.DeepEquals(claimedPolicy, recoveredPolicy) &&
            interruptedBefore.Dispatch is not null && interruptedAfter.Dispatch == interruptedBefore.Dispatch &&
            interruptedAfter.Result is null && interruptedAfter.Retry is null,
            "queue: recovery retains interrupted claim configuration/admission evidence without inventing an HTTP result");
        check(reopened.Cancel(interrupted) == 0 && reopened.Cancel() == 2 &&
            reopened.Get(interrupted)?.State == JobState.Unknown && reopened.Get(completedId)?.State == JobState.Succeeded &&
            reopened.Get(pendingId)?.State == JobState.Cancelled && reopened.Get(retryId)?.State == JobState.Cancelled && !reopened.HasWork(),
            "queue: cancel removes only pending/retry, preserving unknown and successful results");
        var again = new QueueStore(reopenedPaths);
        check(again.Get(pendingId)?.State == JobState.Cancelled && again.Get(retryId)?.State == JobState.Cancelled &&
            again.Get(interrupted)?.State == JobState.Unknown, "queue: cancellation persists across another same-path reopen");
    }

    private static void OutputReservations(QueuePaths paths, string key, Action<bool, string> check)
    {
        var store = new QueueStore(paths);
        var settings = paths.LoadSettings();
        var options = Options(paths, key, "gpt-image-2", "shared-output");
        var id = QueueSubmission.Submit(paths, store, settings, options, "first");
        var rejected = false;
        try
        {
            QueueSubmission.Submit(paths, store, settings, options with
            { ImageModel = "gpt-image-2.5-flare", LogicalImageModel = "gpt-image-2.5-flare", Overwrite = true }, "duplicate");
        }
        catch (CliException) { rejected = true; }
        check(rejected && store.List().Count == 1 && store.Get(id)?.Attempts == 0,
            "queue: duplicate output is rejected transactionally across groups even with overwrite, before any paid request");
        rejected = false;
        try { QueueSubmission.Submit(paths, store, settings, options with
            { OutputFormat = "jpeg", OutputPath = Path.ChangeExtension(options.OutputPath, ".jpg") }, "same-log"); }
        catch (CliException) { rejected = true; }
        check(rejected && store.List().Count == 1,
            "queue: different image extensions cannot reserve the same failure TXT concurrently");
        store.Cancel(id);
        var next = QueueSubmission.Submit(paths, store, settings, options, "after-cancel");
        var running = Claim(store, settings.ForModel("gpt-image-2"), Start);
        check(running.Id == next, "queue: cancelling pending job releases its output reservation");
        using (var lease = paths.TryLock("worker")) store.RecoverInterrupted();
        rejected = false;
        try { QueueSubmission.Submit(paths, store, settings, options, "after-unknown"); }
        catch (CliException) { rejected = true; }
        check(rejected && store.Get(next)?.State == JobState.Unknown,
            "queue: result-unknown job retains output protection until manual review");

        var countBefore = store.List().Count;
        foreach (var extension in new[] { ".json", ".jsonl", ".log", ".txt", ".jpg" })
        {
            rejected = false;
            try { QueueSubmission.Submit(paths, store, settings, options with
                { OutputPath = Path.Combine(paths.Root, "output", "invalid" + extension), Overwrite = true }, "invalid-output"); }
            catch (CliException) { rejected = true; }
            check(rejected && store.List().Count == countBefore,
                "queue: non-image or mismatched output rejected before enqueue, even with overwrite: " + extension);
        }
        rejected = false;
        try { ImageResultWriter.ValidateOutputTargets(options with
            { Mode = ApiMode.Responses, OutputPath = Path.Combine(paths.Root, "output", "invalid.json") }); }
        catch (CliException) { rejected = true; }
        check(rejected, "queue: Responses cannot bypass image extension validation");
        foreach (var output in new[] { Path.Combine(paths.Root, "mixed.png"), Path.Combine(Path.GetDirectoryName(paths.Root)!, "mixed.png") })
        {
            rejected = false;
            try { QueueSubmission.Submit(paths, store, settings, options with { OutputPath = output }, "mixed-runtime"); }
            catch (CliException) { rejected = true; }
            check(rejected && store.List().Count == countBefore, "queue: delivery directory cannot contain queue runtime files");
        }
        foreach (var (format, extension) in new[] { ("png", ".PNG"), ("jpeg", ".jpg"), ("jpeg", ".jpeg"), ("webp", ".webp") })
            check(ImageResultWriter.ResolveOutputPaths(Path.Combine(paths.Root, "output", "valid" + extension), format, 1).Count == 1,
                "queue: accepted image output " + extension);
    }

    private static void Snapshot(QueuePaths paths, string source, byte[] png, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        var policy = settings.ForModel("gpt-image-2");
        var store = new QueueStore(paths);
        // Keep one connection open so SQLite cannot delete/checkpoint away the WAL before inspection.
        using var keepWal = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabaseFile, Pooling = false }.ToString());
        keepWal.Open();
        using (var command = keepWal.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM jobs";
            command.ExecuteScalar();
        }

        var reference = Path.Combine(paths.Root, "owned-reference.png");
        var mask = Path.Combine(paths.Root, "owned-mask.png");
        File.Copy(source, reference, overwrite: false);
        File.Copy(source, mask, overwrite: false);
        var target = Path.Combine(paths.Root, "output", "snapshot.webp");
        var options = Options(paths, key, "gpt-image-2", "snapshot") with
        {
            Mode = ApiMode.Edit, ReferenceImagePaths = [reference], MaskPath = mask,
            ImageModel = "private-deployment-name", LogicalImageModel = "gpt-image-2",
            ConfiguredRequestUrl = "https://queue-tests.invalid/custom/images/edits?api-version=old",
            OutputFormat = "webp", OutputCompression = 73, Count = 2, Overwrite = true,
            OutputPath = Path.GetRelativePath(Environment.CurrentDirectory, target)
        };
        ParameterValidation.Validate(options);
        var id = QueueSubmission.Submit(paths, store, settings, options, "encrypted-snapshot");
        // Delete only the two copies created above, never source or anything outside this case directory.
        File.Delete(reference);
        File.Delete(mask);
        var claimed = Claim(store, policy, Start);
        var decoded = QueueSubmission.Decode(claimed.Payload);
        check(claimed.Id == id && decoded.ApiKey == key && Path.IsPathFullyQualified(decoded.OutputPath) &&
            decoded.OutputPath == target, "queue: encrypted options decode with fake key and absolute output path");
        check(decoded.ReferenceImagePaths.Count == 1 && Inside(paths.InputsDirectory, decoded.ReferenceImagePaths[0]) &&
            decoded.MaskPath is { } savedMask && Inside(paths.InputsDirectory, savedMask) &&
            File.ReadAllBytes(decoded.ReferenceImagePaths[0]).SequenceEqual(png) && File.ReadAllBytes(savedMask).SequenceEqual(png) &&
            !File.Exists(reference) && !File.Exists(mask), "queue: reference and mask snapshots survive removal of owned input copies");
        var expected = options with
        {
            OutputPath = target, ReferenceImagePaths = decoded.ReferenceImagePaths, MaskPath = decoded.MaskPath
        };
        check(JsonSerializer.Serialize(decoded, QueueJsonContext.Default.CliOptions) ==
            JsonSerializer.Serialize(expected, QueueJsonContext.Default.CliOptions),
            "queue: every CliOptions field survives encrypted snapshot round-trip");
        store.Complete(claimed, policy, Limited(key, 61), 1, Start + 1);
        check(!ContainsSecret(claimed.Payload, key) && !store.Get(id)!.Result!.Value.GetRawText().Contains(key),
            "queue: encrypted payload and persisted HTTP error report exclude fake plaintext secret");
        var echoedHeader = Limited(key, 61, requestId: key);
        check(echoedHeader.RequestId is null && !echoedHeader.Serialize(1).Contains(key),
            "queue: a server echoing the key in request-id cannot leak it into attempt logs");
        foreach (var path in new[] { paths.DatabaseFile, paths.DatabaseFile + "-wal" })
        {
            check(File.Exists(path), "queue: inspect existing " + Path.GetFileName(path));
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            file.CopyTo(bytes);
            check(bytes.Length > 0 && !ContainsSecret(bytes.ToArray(), key),
                "queue: " + Path.GetFileName(path) + " contains no UTF-8/UTF-16 fake secret plaintext");
        }
    }

    private static async Task LegacyWorkerAsync(QueuePaths paths, Action<bool, string> check)
    {
        var store = new QueueStore(paths);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var legacy = Path.Combine(paths.Root, "worker-info.json");
        File.WriteAllText(legacy, JsonSerializer.Serialize(new QueueWorkerInfo(1, process.Id,
            process.StartTime.ToUniversalTime().Ticks, "older-binary"), QueueJsonContext.Default.QueueWorkerInfo));
        using var lifecycle = paths.TryLock("lifecycle");
        using var worker = paths.TryLock("worker");
        check(lifecycle is not null && worker is not null, "queue upgrade: own the simulated old worker and lifecycle leases");
        var rejected = false;
        try { await QueueWorkerHost.EnsureStartedUnderLockAsync(paths); }
        catch (CliException ex) { rejected = ex.Message.Contains("另一个版本的执行器"); }
        check(rejected && File.Exists(legacy) && store.GetWorkerInfo() is null,
            "queue upgrade: read-only legacy handshake rejects a live different binary without replacing or restarting it");
    }

    private static async Task WorkerAsync(QueuePaths paths, string source, byte[] png, string key, Action<bool, string> check)
    {
        var settings = paths.LoadSettings();
        foreach (var policy in settings.Queues)
        {
            policy.RequestsPerMinute = 10000;
            policy.RetryDelaySeconds = 1;
        }
        paths.SaveSettings(settings);
        var store = new QueueStore(paths);
        var legacyReady = Path.Combine(paths.Root, "worker-info.json");
        File.WriteAllText(legacyReady, JsonSerializer.Serialize(new QueueWorkerInfo(1, 0, 0, "legacy"), QueueJsonContext.Default.QueueWorkerInfo));
        var reference = Path.Combine(paths.Root, "worker-owned-reference.png");
        File.Copy(source, reference, overwrite: false);
        var editOptions = Options(paths, key, "gpt-image-2", "worker-edit") with
        {
            Mode = ApiMode.Edit, ReferenceImagePaths = [reference, reference], MaskPath = reference,
            ConfiguredRequestUrl = "https://queue-tests.invalid/custom/images/edits?api-version=old"
        };
        var flareOptions = Options(paths, key, "gpt-image-2.5-flare", "worker-flare") with { AuthMode = AuthMode.Bearer };
        var editId = QueueSubmission.Submit(paths, store, settings, editOptions, "worker-edit");
        var flareId = QueueSubmission.Submit(paths, store, settings, flareOptions, "worker-flare");
        File.Delete(reference); // Only our local copy; the worker must use durable snapshots.

        var requests = new List<HttpRequestMessage>();
        var contents = new List<HttpContent>();
        var imageParts = new List<HttpContent>();
        var flareCalls = 0;
        var flareSawRetry = false;
        // A synchronous in-memory handler makes first-429 completion precede the next group
        // deterministically. It consumes the ACTUAL multipart streams on every new request.
        using var handler = new FakeHandler((request, cancellation) =>
        {
            check(paths.WorkerRunning(), "queue worker: exclusive lease held during HTTP");
            check(request.Method == HttpMethod.Post && request.RequestUri?.Host == "queue-tests.invalid",
                "queue worker: only explicit offline POST target used");
            if (request.Content is MultipartFormDataContent multipart)
            {
                check(request.RequestUri!.PathAndQuery == "/custom/images/edits?api-version=queue-test" &&
                    request.Headers.TryGetValues("api-key", out var keys) && keys.Single() == key,
                    "queue worker: restored configured route, API version and fake API-key authentication");
                check(!requests.Any(r => ReferenceEquals(r, request)) && !contents.Any(c => ReferenceEquals(c, multipart)),
                    "queue worker: each attempt rebuilds request and multipart independently");
                requests.Add(request);
                contents.Add(multipart);
                var parts = multipart.ToList();
                var fields = parts.Where(p => p.Headers.ContentDisposition!.FileName is null)
                    .ToDictionary(PartName, p => p.ReadAsStringAsync(cancellation).GetAwaiter().GetResult());
                var expectedFields = new Dictionary<string, string>
                {
                    ["model"] = editOptions.ImageModel, ["prompt"] = editOptions.Prompt, ["size"] = editOptions.Size,
                    ["quality"] = editOptions.Quality, ["output_format"] = "png", ["n"] = "1",
                    ["background"] = "opaque", ["moderation"] = "low", ["user"] = editOptions.User!
                };
                check(fields.Count == expectedFields.Count && expectedFields.All(p => fields.GetValueOrDefault(p.Key) == p.Value),
                    "queue worker: actual multipart fields match submitted options");
                var images = parts.Where(p => PartName(p) == "image[]").ToArray();
                var mask = parts.Single(p => PartName(p) == "mask");
                check(images.Length == 2 && images.All(p => !imageParts.Any(old => ReferenceEquals(old, p))) &&
                    images.Select(p => p.Headers.ContentDisposition!.FileName!.Trim('"')).SequenceEqual(new[] { "reference-0.png", "reference-1.png" }) &&
                    mask.Headers.ContentDisposition!.FileName!.Trim('"') == "mask.png" &&
                    images.Append(mask).All(p => p.Headers.ContentType?.MediaType == "image/png" &&
                        p.ReadAsByteArrayAsync(cancellation).GetAwaiter().GetResult().SequenceEqual(png)),
                    "queue worker: fresh reference multipart parts and mask read original PNG bytes on every attempt");
                imageParts.AddRange(images);
                return requests.Count == 1 ? LimitedResponse(key, 1) : ImageResponse(png);
            }

            flareCalls++;
            check(request.RequestUri!.PathAndQuery == "/v1/images/generations?api-version=queue-test" &&
                request.Headers.Authorization is { Scheme: "Bearer" } auth && auth.Parameter == key &&
                request.Content?.Headers.ContentType?.MediaType == "application/json",
                "queue worker: independent flare JSON route and fake bearer authentication");
            using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellation).GetAwaiter().GetResult());
            check(json.RootElement.GetProperty("model").GetString() == flareOptions.ImageModel &&
                json.RootElement.GetProperty("prompt").GetString() == flareOptions.Prompt,
                "queue worker: flare request uses its own submitted options");
            flareSawRetry = store.Get(editId)?.State == JobState.Retry;
            check(!File.Exists(Path.ChangeExtension(editOptions.OutputPath, ".txt")),
                "queue worker: retry_wait does not write a premature failure log");
            return ImageResponse(png);
        });
        // Only this integration case uses real time (~1s retry + worker dispatch ticks).
        // The timeout bounds regressions; elapsed wall-clock speed is deliberately not asserted.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exit = await QueueWorker.RunAsync(paths, timeout.Token, handler);
        check(exit == 0 && !timeout.IsCancellationRequested && requests.Count == 2 && flareCalls == 1,
            "queue worker: drains with exactly one 429 retry and one independent successful request");
        var edit = store.Get(editId)!;
        var flareJob = store.Get(flareId)!;
        var history = store.Attempts(editId);
        check(edit.State == JobState.Succeeded && edit.Attempts == 2 && history.Count == 2 &&
            history[0].HttpStatus == 429 && history[1].HttpStatus == 200 && history[0].FinishedAt.HasValue &&
            history[1].StartedAt >= history[0].FinishedAt.GetValueOrDefault() + 1000,
            "queue worker: persists first 429 then 200, respecting one-second cooldown");
        check(flareSawRetry && flareJob.State == JobState.Succeeded && flareJob.Attempts == 1 &&
            store.Attempts(flareId).Single().StartedAt < history[1].StartedAt,
            "queue worker: flare succeeds independently while image2 waits to retry");
        foreach (var (job, expectedPath) in new[] { (edit, editOptions.OutputPath), (flareJob, flareOptions.OutputPath) })
        {
            var result = job.Result!.Value;
            var files = result.GetProperty("files").EnumerateArray().Select(f => f.GetString()!).ToArray();
            check(result.GetProperty("ok").GetBoolean() && files.SequenceEqual(new[] { expectedPath }) &&
                files.All(p => Inside(paths.Root, p) && File.Exists(p) && File.ReadAllBytes(p).SequenceEqual(png)),
                "queue worker: successful result lists actual saved PNG " + job.Name);
        }
        foreach (var input in Directory.GetFiles(paths.InputsDirectory, "*", SearchOption.AllDirectories))
        {
            using var exclusive = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.None);
            check(exclusive.Length == png.Length, "queue worker: multipart disposal releases snapshot " + Path.GetFileName(input));
        }
        check(!store.HasWork() && !paths.WorkerRunning(), "queue worker: drained work exits and releases singleton lease");
        var ready = store.GetWorkerInfo();
        check(ready is { ProtocolVersion: 1 } && ready.ProcessId == Environment.ProcessId &&
            new QueueStore(new QueuePaths(paths.Root)).GetWorkerInfo() == ready && !File.Exists(legacyReady) &&
            !File.Exists(legacyReady + ".tmp"), "queue worker: ready info persists in SQLite, no JSON sidecar remains");
        var outputs = Directory.GetFiles(Path.Combine(paths.Root, "output"));
        check(outputs.Length == 2 && outputs.All(p => Path.GetExtension(p) == ".png"),
            "queue worker: delivery directory contains only the two generated images");
        using var lease = paths.TryLock("worker");
        check(lease is not null, "queue worker: released lease can be acquired again");
    }

    private static async Task FailureOutputsAsync(QueuePaths paths, string source, byte[] png, string key, Action<bool, string> check)
    {
        const string message = "Your request was rejected by the safety system. Include request ID 263d5f70-7fbd-417d-9bb6-ab0da052de76.";
        var settings = paths.LoadSettings();
        foreach (var policy in settings.Queues) policy.RequestsPerMinute = 10000;
        paths.SaveSettings(settings);
        var store = new QueueStore(paths);
        var edit = Options(paths, key, "gpt-image-2.5-flare", "moderation") with
        {
            Mode = ApiMode.Edit, ReferenceImagePaths = [source], OutputFormat = "jpeg",
            OutputPath = Path.Combine(paths.Root, "output", "moderation.jpg")
        };
        var batch = Options(paths, key, "gpt-image-2", "batch") with { Count = 2 };
        var auto = Options(paths, key, "gpt-image-2", "auto") with
        { OutputFormat = "webp", OutputPath = Path.Combine(paths.Root, "output", "automatic") + Path.DirectorySeparatorChar };
        var blocked = Options(paths, key, "gpt-image-2", "blocked");
        var occupied = Options(paths, key, "gpt-image-2", "occupied");
        var unknown = Options(paths, key, "gpt-image-2", "unknown");
        var cases = new[] { edit, batch, auto, blocked, occupied, unknown };
        var jobs = cases.Select(o => QueueSubmission.Submit(paths, store, settings, o, null)).ToArray();
        Directory.CreateDirectory(Path.ChangeExtension(blocked.OutputPath, ".txt"));
        var occupiedLog = Path.ChangeExtension(occupied.OutputPath, ".txt");
        File.WriteAllText(occupiedLog, "existing user text");
        using var handler = new FakeHandler((request, cancellation) =>
        {
            if (request.Content is not MultipartFormDataContent)
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync(cancellation).GetAwaiter().GetResult());
                if (body.RootElement.GetProperty("prompt").GetString() == unknown.Prompt)
                    throw new HttpRequestException("offline connection failure");
            }
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { error = new { code = "moderation_blocked", message = message + " api-key=" + key } }), Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-request-id", "263d5f70-7fbd-417d-9bb6-ab0da052de76");
            return response;
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        check(await QueueWorker.RunAsync(paths, timeout.Token, handler) == 0 && !timeout.IsCancellationRequested,
            "failure output: worker drains despite unwritable TXT and an existing user file");
        foreach (var id in jobs)
        {
            var job = store.Get(id)!;
            var isUnknown = id == jobs[^1];
            check(job.State == (isUnknown ? JobState.Unknown : JobState.Failed) && job.Attempts == 1 &&
                job.HttpStatus == (isUnknown ? null : 400) && store.Attempts(id).Single().Result is not null &&
                job.Result!.Value.GetProperty("files").GetArrayLength() == 0,
                "failure output: original job and attempt persist, TXT is not an image result " + id);
        }
        foreach (var (path, id) in new[]
        {
            (Path.ChangeExtension(edit.OutputPath, ".txt"), jobs[0]),
            (Path.Combine(paths.Root, "output", "batch-01.txt"), jobs[1]),
            (Path.Combine(paths.Root, "output", "batch-02.txt"), jobs[1]),
            (Directory.GetFiles(auto.OutputPath, "*.txt").Single(), jobs[2])
        })
        {
            var text = File.ReadAllText(path);
            using var json = JsonDocument.Parse(text);
            var log = json.RootElement;
            var dbResult = store.Get(id)!.Result!.Value;
            check(log.GetProperty("Id").GetInt64() == id && log.GetProperty("State").GetString() == "failed" &&
                log.GetProperty("Http").GetInt32() == 400 && log.GetProperty("ErrorCode").GetString() == "moderation_blocked" &&
                log.GetProperty("UpstreamMessage").GetString() == dbResult.GetProperty("api_error").GetProperty("upstream_message").GetString() &&
                log.GetProperty("UpstreamMessage").GetString()!.StartsWith(message) && !text.Contains(key) &&
                log.GetProperty("FileCount").GetInt32() == 0 &&
                log.GetProperty("Mode").GetString() == (id == jobs[0] ? "edit" : "images") &&
                log.GetProperty("Model").GetString() == (id == jobs[0] ? edit.ImageModel : batch.ImageModel),
                "failure output: same-name TXT preserves safe upstream detail and task metadata " + Path.GetFileName(path));
        }
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(unknown.OutputPath, ".txt"))))
            check(json.RootElement.GetProperty("State").GetString() == "unknown" &&
                json.RootElement.GetProperty("UpstreamMessage").GetString() == store.Get(jobs[^1])!.Error,
                "failure output: uncertain network result stays unknown with original local error fallback");
        check(File.ReadAllText(occupiedLog) == "existing user text" &&
            Directory.Exists(Path.ChangeExtension(blocked.OutputPath, ".txt")) &&
            !Directory.GetFiles(Path.Combine(paths.Root, "output"), "*", SearchOption.AllDirectories)
                .Any(p => Path.GetExtension(p) != ".txt"),
            "failure output: no fake images, existing files/directories unchanged");

        // The legacy synchronous command shares the output branch, without creating queue history.
        var syncImage = Path.Combine(paths.Root, "output", "sync.png");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var syncExit = await CliApplication.RunAsync(["--no-config", "--endpoint", "https://queue-tests.invalid",
            "--api-key", key, "--prompt", "offline sync", "--output", syncImage, "--json"], output, error, handler);
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(syncImage, ".txt"))))
            check(syncExit == 1 && json.RootElement.GetProperty("Id").ValueKind == JsonValueKind.Null &&
                json.RootElement.GetProperty("ErrorCode").GetString() == "moderation_blocked" && !File.Exists(syncImage),
                "failure output: synchronous CLI also writes same-name TXT and preserves failure exit code");

        // Explicit overwrite may replace a TXT, but partial successes never get a failure placeholder.
        var report = new CliReport { Error = "local write failure", Mode = "images" };
        var partial = batch with { OutputPath = Path.Combine(paths.Root, "output", "partial.png") };
        var partialPaths = ImageResultWriter.ResolveOutputPaths(partial.OutputPath, partial.OutputFormat, partial.Count);
        File.WriteAllBytes(partialPaths[0], png);
        report.Files.Add(partialPaths[0]);
        check(await FailureResultWriter.TrySaveAsync(partial, report, JobState.Failed) &&
            !File.Exists(Path.ChangeExtension(partialPaths[0], ".txt")) &&
            File.Exists(Path.ChangeExtension(partialPaths[1], ".txt")) && File.ReadAllBytes(partialPaths[0]).SequenceEqual(png),
            "failure output: partial success keeps its image; only missing image gets TXT");
        check(await FailureResultWriter.TrySaveAsync(occupied with { Overwrite = true }, report, JobState.Failed) &&
            File.ReadAllText(occupiedLog).Contains("local write failure"),
            "failure output: explicit overwrite allows replacing an existing TXT");
    }

    private static CliOptions Options(QueuePaths paths, string key, string model, string name) => new()
    {
        Endpoint = "https://queue-tests.invalid", ApiKey = key, ApiKeySource = "离线队列测试",
        Prompt = "离线测试 " + name, Mode = ApiMode.Images, ReferenceImagePaths = [], ImageAction = null,
        AuthMode = AuthMode.ApiKey, TextModel = "offline-text-model", ImageModel = model, LogicalImageModel = model,
        ConfiguredRequestUrl = null, ApiVersion = "queue-test", Size = "1024x640", Quality = "high",
        OutputFormat = "png", Count = 1, OutputPath = Path.Combine(paths.Root, "output", name + ".png"),
        Overwrite = false, TimeoutMinutes = 1, MaskPath = null, Background = "opaque",
        OutputCompression = null, Moderation = "low", User = "offline-queue-user"
    };

    private static long Submit(QueuePaths paths, QueueStore store, QueueSettings settings, string key, string model, string name) =>
        QueueSubmission.Submit(paths, store, settings, Options(paths, key, model, name), name);

    private static ClaimedJob Claim(QueueStore store, QueueDefinition policy, long now) =>
        store.TryClaim(policy, now) ?? throw new Exception($"FAIL: queue {policy.Id} expected a claim at virtual time {now}");

    private static bool SameAttempts(List<QueueAttempt> left, List<QueueAttempt> right) =>
        JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(new QueueReply(true, "", Attempts: left), QueueJsonContext.Default.QueueReply),
            JsonSerializer.SerializeToElement(new QueueReply(true, "", Attempts: right), QueueJsonContext.Default.QueueReply));

    private static bool SameReport(JsonElement left, JsonElement right) =>
        left.EnumerateObject().Count() == right.EnumerateObject().Count() &&
        left.EnumerateObject().All(p => right.TryGetProperty(p.Name, out var value) &&
            (p.Name == "elapsed_ms" || JsonElement.DeepEquals(p.Value, value)));

    private static CliReport Success()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var report = new CliReport { Stage = "save", Mode = "images" };
        report.CaptureHeaders(response);
        return report;
    }

    private static CliReport Limited(string key, int seconds, string code = "rate_limit_exceeded", string requestId = "queue-offline-request")
    {
        using var response = LimitedResponse(key, seconds, code, requestId);
        var report = new CliReport { Stage = "response", Mode = "images", Error = "HTTP 429：请求失败。" };
        report.CaptureHeaders(response);
        report.CaptureBody(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), key);
        return report;
    }

    private static HttpResponseMessage LimitedResponse(string key, int seconds, string code = "rate_limit_exceeded", string requestId = "queue-offline-request")
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                error = new { code, type = key, param = "image[0]", message = "never persist " + key }
            }), Encoding.UTF8, "application/json")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        response.Headers.Add("x-request-id", requestId);
        return response;
    }

    private static HttpResponseMessage ImageResponse(byte[] png) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        { data = new[] { new { b64_json = Convert.ToBase64String(png) } } }), Encoding.UTF8, "application/json")
    };

    private static bool Inside(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSecret(byte[] bytes, string key) =>
        bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(key)) >= 0 ||
        bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(key)) >= 0 ||
        bytes.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes(key)) >= 0;

    private static string PartName(HttpContent part) => part.Headers.ContentDisposition!.Name!.Trim('"');

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, cancellationToken));
        }
    }
}