using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GptImageCli;

internal static class DiagnosticsTests
{
    private const string Key = "offline-diagnostic_key+/?";
    private const string Prompt = "private-prompt-do-not-log";

    public static async Task RunAsync(string root, byte[] png, Action<bool, string> check)
    {
        foreach (var name in DiagnosticHeaders.RequestIds)
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation(name, "request-offline-123");
            report.CaptureHeaders(response, Key);
            var json = Read(report);
            check(report.RequestId == "request-offline-123" &&
                json.GetProperty("request_id_source").GetString() == name &&
                json.GetProperty("response_headers").GetProperty(name).GetString() == report.RequestId,
                "diagnostics: request ID header variant " + name);
        }
        foreach (var length in new[] { 256, 257 })
        {
            var id = new string('x', length);
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation("x-request-id", id);
            report.CaptureHeaders(response, Key);
            check(report.RequestId == (length == 256 ? id : null),
                "diagnostics: request ID header preserves legacy 256-character bound");
            report = new CliReport();
            report.CaptureBody(JsonSerializer.Serialize(new { request_id = id }), Key);
            check(report.RequestId == (length == 256 ? id : null),
                "diagnostics: body request ID uses the same safe bound");
        }

        foreach (var (body, expected, status, source) in new[]
        {
            ("{}", (string?)null, "missing", (string?)null),
            ("{\"request_id\":42}", null, "unusable", null),
            ("{\"request_id\":\"bad\\nvalue\"}", null, "unusable", null),
            ("{\"request_id\":\"request-body\"}", "request-body", "available", "body.request_id"),
            ("{\"error\":{\"requestId\":\"request-error\"}}", "request-error", "available", "body.error.requestId"),
            ("{\"id\":\"not-a-request-id\"}", null, "missing", null)
        })
        {
            var report = new CliReport();
            report.CaptureBody(body, Key);
            var json = Read(report);
            check(report.RequestId == expected && json.GetProperty("request_id_status").GetString() == status &&
                json.GetProperty("request_id_source").GetString() == source, "diagnostics: explicit request ID status/fallback " + body);
        }

        var secretForms = new[]
        {
            Key, Uri.EscapeDataString(Key), Uri.EscapeDataString(Key).ToLowerInvariant(),
            Uri.EscapeDataString(Uri.EscapeDataString(Key)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Key)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Key)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            Prompt, "https://private.invalid/image?sig=private", "******", "sk-unrelated-secret",
            "line\r\nbreak", new string('x', 10000)
        };
        foreach (var value in secretForms)
        {
            var report = new CliReport();
            report.ProtectInputs(Options(root));
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation("x-request-id", value);
            response.Headers.TryAddWithoutValidation("retry-after", value);
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", value);
            response.Headers.TryAddWithoutValidation("authorization", value);
            response.Headers.TryAddWithoutValidation("set-cookie", value);
            response.Headers.TryAddWithoutValidation("x-not-allowed", value);
            report.CaptureHeaders(response, Key);
            report.CaptureBody(JsonSerializer.Serialize(new { error = new { code = value, type = value, message = value } }), Key);
            var json = Read(report);
            check(report.RequestId is null && json.GetProperty("request_id_status").GetString() == "unusable" &&
                json.GetProperty("response_headers").EnumerateObject().Count() == 0 &&
                json.GetProperty("api_error").GetProperty("code").ValueKind == JsonValueKind.Null &&
                json.GetProperty("api_error").GetProperty("type").ValueKind == JsonValueKind.Null &&
                !report.Serialize(1).Contains(value, StringComparison.Ordinal),
                "diagnostics: untrusted headers/error identifiers reject sensitive and encoded values");
        }

        foreach (var (reason, expected) in new[]
        {
            ("Rate limit exceeded", "频率"), ("Insufficient quota", "额度"), ("Server overloaded", "容量"),
            ("Invalid API key", "认证"), ("content_policy_violation", "安全"), ("Request timed out", "超时"),
            ("unsupported image size", "参数"), ("model_not_found", "模型"), ("invalid_api_key", "认证"),
            ("permission_denied", "权限"), ("server_error", "内部错误")
        })
        {
            var report = new CliReport();
            report.ProtectInputs(Options(root));
            var malicious = reason + " prompt=" + Prompt + " Authorization: Bearer " + Key +
                " data:image/png;base64," + Convert.ToBase64String(png) + " https://private.invalid?sig=private\nDO NOT LOG";
            report.CaptureBody(JsonSerializer.Serialize(new { error = new { message = malicious } }), Key);
            var summary = Read(report).GetProperty("api_error").GetProperty("message").GetString()!;
            check(summary.Contains(expected) && summary.Length < 100 &&
                !report.Serialize(1).Contains(Prompt) && !report.Serialize(1).Contains(Key) &&
                !report.Serialize(1).Contains("private.invalid") && !report.Serialize(1).Contains(Convert.ToBase64String(png)),
                "diagnostics: bounded canonical reason survives hostile message without echo: " + reason);
        }
        foreach (var message in new[] { Key, new string('x', 20000), "<script>echo secrets</script>" })
        {
            var report = new CliReport();
            report.CaptureBody(JsonSerializer.Serialize(new { error = new { message } }), Key);
            check(Read(report).GetProperty("api_error").GetProperty("message").GetString() == "服务端返回错误。",
                "diagnostics: unknown or oversized message stays generic");
        }
        foreach (var (message, expected) in new[]
        {
            ("Rate limit reached on tokens per min (TPM): Limit 30000, Used 28000, Requested 4000", "TPM"),
            ("token-per-minute quota exceeded", "TPM"), ("Rate limit reached on requests per min (RPM)", "RPM"),
            ("request-per-minute quota exceeded", "RPM"), ("Too many concurrent requests", "并发请求数"),
            ("Too many simultaneous operations", "并发请求数"), ("Deployment capacity exhausted", "服务容量"),
            ("Server overloaded", "服务容量"), ("unknown rate limit reached", "具体限额未知")
        })
        {
            var report = new CliReport();
            report.CaptureBody(JsonSerializer.Serialize(new
            {
                error = new { code = "rate_limit_exceeded", type = "rate_limit_error", message = message + " " + Key }
            }), Key);
            var summary = Read(report).GetProperty("api_error").GetProperty("message").GetString()!;
            check(summary.Contains(expected) && summary.Contains("推断") && !summary.Contains(message) &&
                !summary.Contains("30000") && !summary.Contains(Key),
                "diagnostics: inferred limiting dimension outranks generic rate-limit code: " + expected);
        }
        foreach (var name in DiagnosticHeaders.RateLimits.Where(name => name.EndsWith("-limit") || name.Contains("-limit-")))
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation(name, "30000");
            report.CaptureHeaders(response, Key);
            check(Read(report).GetProperty("response_headers").GetProperty(name).GetString() == "30000",
                "diagnostics: numeric limit header retained " + name);
            using var hostile = Response("{}");
            hostile.Headers.TryAddWithoutValidation(name, "30000; prompt=" + Prompt);
            report.CaptureHeaders(hostile, Key);
            check(Read(report).GetProperty("response_headers").EnumerateObject().Count() == 0,
                "diagnostics: hostile limit header omitted " + name);
        }

        foreach (var (name, value, seconds) in new[]
        {
            ("retry-after", "12", 12d), ("retry-after-ms", "250", .25),
            ("x-ms-retry-after-ms", "1500", 1.5), ("retry-after", "0", 0d)
        })
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation(name, value);
            report.CaptureHeaders(response);
            check(report.RetryAfterSeconds == seconds && report.RetryAfterSource == name &&
                Read(report).GetProperty("retry_after_source").GetString() == name,
                "diagnostics: retry seconds/source " + name);
        }
        foreach (var name in DiagnosticHeaders.Retry)
        foreach (var digits in new[] { 24, 400 })
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation(name, new string('9', digits));
            report.CaptureHeaders(response);
            check(double.IsFinite(report.RetryAfterSeconds) && report.RetryAfterSeconds > TimeSpan.FromDays(365).TotalSeconds &&
                report.RetryAfterSource == name, "diagnostics: excessive retry hint remains finite for queue policy " + name);
        }
        foreach (var (standard, milliseconds, azureMilliseconds, seconds, source) in new[]
        {
            ("1", "500", "120000", 120d, "x-ms-retry-after-ms"),
            ("1", "180000", "120000", 180d, "retry-after-ms"),
            ("180", "180000", "180000", 180d, "retry-after"),
            ("1", "180000", "180000", 180d, "retry-after-ms")
        })
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation("retry-after", standard);
            response.Headers.TryAddWithoutValidation("retry-after-ms", milliseconds);
            response.Headers.TryAddWithoutValidation("x-ms-retry-after-ms", azureMilliseconds);
            report.CaptureHeaders(response, Key);
            check(report.RetryAfterSeconds == seconds && report.RetryAfterSource == source &&
                Read(report).GetProperty("response_headers").EnumerateObject().Count() == 3,
                "diagnostics: conflicting retry headers choose maximum delay with stable ties: " + source);
        }
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation("retry-after", DateTimeOffset.UtcNow.AddMinutes(1).ToString("r", CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("retry-after-ms", "250");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset-tokens", "1m30.5s");
            report.CaptureHeaders(response);
            check(report.RetryAfterSeconds is > 55 and <= 60 && report.RetryAfterSource == "retry-after" &&
                Read(report).GetProperty("response_headers").GetProperty("x-ratelimit-reset-tokens").GetString() == "1m30.5s",
                "diagnostics: retry date, precedence and bounded rate-limit duration");
        }
        {
            var report = new CliReport();
            using var response = Response("{}");
            response.Headers.TryAddWithoutValidation("retry-after", "invalid");
            response.Headers.TryAddWithoutValidation("x-ms-retry-after-ms", "500");
            response.Headers.TryAddWithoutValidation("x-request-id", new[] { "first", "second" });
            response.Headers.TryAddWithoutValidation("apim-request-id", "fallback-request");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-tokens", "-1");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset-requests", "10s https://private.invalid");
            report.CaptureHeaders(response);
            check(report.RetryAfterSeconds == .5 && report.RetryAfterSource == "x-ms-retry-after-ms" &&
                report.RequestId == "fallback-request" && Read(report).GetProperty("response_headers").EnumerateObject().Count() == 2,
                "diagnostics: malformed/duplicate headers cannot hide usable fallbacks");
        }

        var b64 = Convert.ToBase64String(png);
        {
            var report = new CliReport();
            report.ProtectInputs(Options(root) with
            {
                Mode = ApiMode.Responses, ImageModel = "deployment-image", LogicalImageModel = "gpt-image-2",
                TextModel = "gpt-4.1", Size = "1024x1024", Quality = "high", Count = 2, TimeoutMinutes = 7
            });
            var settings = Read(report).GetProperty("request_settings");
            check(settings.GetProperty("mode").GetString() == "responses" &&
                settings.GetProperty("image_model").GetString() == "deployment-image" &&
                settings.GetProperty("logical_image_model").GetString() == "gpt-image-2" &&
                settings.GetProperty("text_model").GetString() == "gpt-4.1" &&
                settings.GetProperty("size").GetString() == "1024x1024" &&
                settings.GetProperty("quality").GetString() == "high" &&
                settings.GetProperty("count").GetInt32() == 2 && settings.GetProperty("timeout_minutes").GetInt32() == 7 &&
                settings.EnumerateObject().Count() == 8, "diagnostics: request settings contain only safe effective model/generation settings");
            report.ProtectInputs(Options(root) with
            {
                ImageModel = Prompt, LogicalImageModel = Convert.ToBase64String(Encoding.UTF8.GetBytes(Key)),
                TextModel = "https://private.invalid", Mode = ApiMode.Responses
            });
            settings = Read(report).GetProperty("request_settings");
            check(settings.GetProperty("image_model").ValueKind == JsonValueKind.Null &&
                settings.GetProperty("logical_image_model").ValueKind == JsonValueKind.Null &&
                settings.GetProperty("text_model").ValueKind == JsonValueKind.Null &&
                !report.Serialize(1).Contains(Prompt) && !report.Serialize(1).Contains(Key),
                "diagnostics: settings reject prompt, encoded key and URL disguised as model identifiers");
        }
        var success = await Execute(Options(root), _ => Task.FromResult(Response(JsonSerializer.Serialize(new { data = new[] { new { b64_json = b64 } } }))));
        check(success.Exit == 0 && HasPhases(success.Report, "headers_wait", "body_receive", "parse", "decode", "write") &&
            Read(success.Report).GetProperty("phase_elapsed_ms").GetProperty("download").ValueKind == JsonValueKind.Null,
            "diagnostics: successful base64 attempt records all phases and null unstarted download");
        var downloadOptions = Options(root);
        var downloaded = await Execute(downloadOptions, request => Task.FromResult(request.Method == HttpMethod.Post
            ? Response("{\"data\":[{\"url\":\"https://offline.invalid/image\"}]}")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }));
        check(downloaded.Exit == 0 && HasPhases(downloaded.Report, "download", "write") &&
            Read(downloaded.Report).GetProperty("phase_elapsed_ms").GetProperty("decode").ValueKind == JsonValueKind.Null,
            "diagnostics: download phase is separate from response receive/decode");

        var headersFailure = await Execute(Options(root), _ => throw new HttpRequestException(Key));
        check(headersFailure.Exit == 1 && headersFailure.Report.Stage == "request" &&
            HasPhases(headersFailure.Report, "headers_wait") &&
            Read(headersFailure.Report).GetProperty("phase_elapsed_ms").GetProperty("body_receive").ValueKind == JsonValueKind.Null,
            "diagnostics: header failure preserves failed phase duration");
        var bodyFailure = await Execute(Options(root), _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new FailingContent() }));
        check(bodyFailure.Exit == 1 && bodyFailure.Report.Stage == "request" && bodyFailure.Report.HttpStatus == 200 &&
            HasPhases(bodyFailure.Report, "headers_wait", "body_receive") &&
            Read(bodyFailure.Report).GetProperty("phase_elapsed_ms").GetProperty("parse").ValueKind == JsonValueKind.Null,
            "diagnostics: body failure retains unknown post-send stage and receive timing");
        var canceled = await Execute(Options(root), _ => throw new OperationCanceledException(Key));
        check(canceled.Exit == 1 && canceled.Report.Stage == "request" && HasPhases(canceled.Report, "headers_wait") &&
            !canceled.Report.Serialize(1).Contains(Key), "diagnostics: cancellation preserves timing without leaking exception text");
        var echoedOnFailure = await Execute(Options(root), _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() };
            response.Headers.TryAddWithoutValidation("x-request-id", Uri.EscapeDataString(Key));
            return Task.FromResult(response);
        });
        check(echoedOnFailure.Report.RequestId is null &&
            Read(echoedOnFailure.Report).GetProperty("request_id_status").GetString() == "unusable" &&
            !echoedOnFailure.Report.Serialize(1).Contains(Uri.EscapeDataString(Key)),
            "diagnostics: secret header is rejected even when body cannot be received");
        var parseFailure = await Execute(Options(root), _ => Task.FromResult(Response("not JSON")));
        check(parseFailure.Exit == 1 && parseFailure.Report.Stage == "response" && HasPhases(parseFailure.Report, "parse"),
            "diagnostics: parse failure retains parse timing");
        var decodeFailure = await Execute(Options(root), _ => Task.FromResult(Response("{\"data\":[{\"b64_json\":\"bad base64\"}]}")));
        check(decodeFailure.Exit == 1 && decodeFailure.Report.Stage == "save" && HasPhases(decodeFailure.Report, "decode"),
            "diagnostics: decode failure retains decode timing");
        var downloadFailure = await Execute(Options(root), request => request.Method == HttpMethod.Post
            ? Task.FromResult(Response("{\"data\":[{\"url\":\"https://offline.invalid/image\"}]}"))
            : throw new HttpRequestException(Key));
        check(downloadFailure.Exit == 1 && downloadFailure.Report.Stage == "save" && HasPhases(downloadFailure.Report, "download"),
            "diagnostics: image download failure retains download timing");
        var writeOptions = Options(root);
        var writeFailure = await Execute(writeOptions, async _ =>
        {
            await File.WriteAllTextAsync(writeOptions.OutputPath, "existing");
            return Response("{\"data\":[{\"b64_json\":\"" + b64 + "\"}]}");
        });
        check(writeFailure.Exit == 1 && HasPhases(writeFailure.Report, "write") &&
            await File.ReadAllTextAsync(writeOptions.OutputPath) == "existing",
            "diagnostics: write failure preserves timing and existing file");
        var timings = new PhaseTimings();
        var original = new IOException("original");
        try
        {
            using (timings.Measure("write")) throw original;
        }
        catch (IOException caught)
        {
            check(ReferenceEquals(caught, original) && timings.ElapsedMilliseconds["write"] is >= 0,
                "diagnostics: timing scope never replaces the original exception");
        }
    }

    private static CliOptions Options(string root) => new()
    {
        Endpoint = "https://offline.invalid", ApiKey = Key, Prompt = Prompt,
        OutputPath = Path.Combine(root, $"diagnostic-{Guid.NewGuid():N}.png")
    };

    private static bool HasPhases(CliReport report, params string[] names) =>
        names.All(name => report.Timings.ElapsedMilliseconds[name] is >= 0);

    private static JsonElement Read(CliReport report)
    {
        using var json = JsonDocument.Parse(report.Serialize(1));
        return json.RootElement.Clone();
    }

    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static async Task<(int Exit, CliReport Report)> Execute(CliOptions options, Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var report = new CliReport();
        using var handler = new Handler(respond);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.ExecuteAsync(options, output, error, report, handler);
        return (exit, report);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FailingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("offline body receive failure");
    }
}
