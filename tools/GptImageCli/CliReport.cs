using System.Diagnostics;
using System.Text.Json;

namespace GptImageCli;

internal sealed class CliReport
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public string? Mode { get; set; }
    public string? Error { get; set; }
    public EndpointSummary[]? Endpoints { get; set; }
    public List<string> Files { get; } = [];
    public int? HttpStatus => _httpStatus;
    public string? RequestId => _requestId;
    public string? ApiErrorCode => _apiError?.code;
    public double RetryAfterSeconds { get; private set; }
    public string? RetryAfterSource { get; private set; }
    public string Stage { get; set; } = "prepare";
    public PhaseTimings Timings { get; } = new();
    private int? _httpStatus;
    private string? _requestId;
    private object? _usage;
    private ApiErrorReport? _apiError;
    private RequestSettingsReport? _requestSettings;
    private readonly List<Dictionary<string, string>> _metadata = [];
    private readonly Dictionary<string, string> _responseHeaders = [];
    private readonly DiagnosticSanitizer _sanitizer = new();
    private string _requestIdStatus = "missing";
    private string? _requestIdSource;

    internal void ProtectInputs(CliOptions options)
    {
        _sanitizer.AddSensitiveValue(options.ApiKey);
        _sanitizer.AddSensitiveValue(options.Prompt);
        _requestSettings = new RequestSettingsReport(
            options.Mode.ToString().ToLowerInvariant(),
            _sanitizer.Identifier(options.ImageModel, 128),
            _sanitizer.Identifier(options.LogicalImageModel ?? options.ImageModel, 128),
            options.Mode == ApiMode.Responses ? _sanitizer.Identifier(options.TextModel, 128) : null,
            ValidMetadata("size", options.Size) ? _sanitizer.Identifier(options.Size) : null,
            ValidMetadata("quality", options.Quality) ? _sanitizer.Identifier(options.Quality) : null,
            options.Count, options.TimeoutMinutes);
    }

    public void CaptureHeaders(HttpResponseMessage response, string? apiKey = null)
    {
        _sanitizer.AddSensitiveValue(apiKey);
        _httpStatus = (int)response.StatusCode;
        _responseHeaders.Clear();
        _requestId = null;
        _requestIdSource = null;
        _requestIdStatus = "missing";
        RetryAfterSeconds = 0;
        RetryAfterSource = null;
        foreach (var name in DiagnosticHeaders.RequestIds.Concat(DiagnosticHeaders.Retry).Concat(DiagnosticHeaders.RateLimits))
        {
            if (!response.Headers.TryGetValues(name, out var values)) continue;
            var entries = values.Take(2).ToArray();
            var value = entries.Length == 1 ? entries[0] : null;
            if (DiagnosticHeaders.RequestIds.Contains(name))
            {
                if (_requestId is null) _requestIdStatus = "unusable";
                if (_sanitizer.Identifier(value, 256) is not { } id) continue;
                _responseHeaders[name] = id;
                if (_requestId is not null) continue;
                _requestId = id;
                _requestIdSource = name;
                _requestIdStatus = "available";
            }
            else if (value is { Length: <= 8192 } && !_sanitizer.ContainsSensitiveValue(value))
            {
                if (DiagnosticHeaders.Retry.Contains(name))
                {
                    if (!DiagnosticHeaders.RetryValue(name, value, out var seconds)) continue;
                    _responseHeaders[name] = value.Length <= 128 ? value : "[oversized numeric value omitted]";
                    if (RetryAfterSource is not null && seconds <= RetryAfterSeconds) continue;
                    RetryAfterSeconds = seconds;
                    RetryAfterSource = name;
                }
                else if (value.Length <= 128 && DiagnosticHeaders.RateLimitValue(name, value)) _responseHeaders[name] = value;
            }
        }
    }

    public void CaptureBody(string text, string? apiKey = null)
    {
        RedactTransportMetadata(apiKey);
        _apiError = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var apiError) &&
                apiError.ValueKind == JsonValueKind.Object)
                _apiError = new ApiErrorReport(
                    _sanitizer.ErrorField(apiError, "code"),
                    _sanitizer.ErrorField(apiError, "type"),
                    _sanitizer.ErrorField(apiError, "param"),
                    _sanitizer.ErrorSummary(apiError));
            CaptureBodyRequestId(root, "body");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                CaptureBodyRequestId(error, "body.error");
            Visit(document.RootElement);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("usage", out var usage))
                _usage = NumericUsage(usage);
        }
        catch (JsonException) { /* The response parser reports malformed JSON; retain HTTP metadata. */ }
    }

    internal void RedactTransportMetadata(string? apiKey)
    {
        _sanitizer.AddSensitiveValue(apiKey);
        foreach (var name in _responseHeaders.Keys.ToArray())
            if (_sanitizer.ContainsSensitiveValue(_responseHeaders[name])) _responseHeaders.Remove(name);
        if (_requestId is { } id && _sanitizer.ContainsSensitiveValue(id))
        {
            _requestId = null;
            _requestIdSource = null;
            _requestIdStatus = "unusable";
        }
        if (RetryAfterSource is { } source && !_responseHeaders.ContainsKey(source))
        {
            RetryAfterSeconds = 0;
            RetryAfterSource = null;
        }
        if (Error is { } error && _sanitizer.ContainsSensitiveValue(error)) Error = "任务失败；敏感错误详情已隐藏。";
    }

    private void CaptureBodyRequestId(JsonElement root, string prefix)
    {
        if (_requestId is not null || root.ValueKind != JsonValueKind.Object) return;
        foreach (var name in new[] { "request_id", "requestId", "request-id" })
        {
            if (!root.TryGetProperty(name, out var field)) continue;
            _requestIdStatus = "unusable";
            if (field.ValueKind != JsonValueKind.String || _sanitizer.Identifier(field.GetString(), 256) is not { } id) continue;
            _requestId = id;
            _requestIdSource = prefix + "." + name;
            _requestIdStatus = "available";
            return;
        }
    }

    private static object? NumericUsage(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.Clone(),
        JsonValueKind.Object => value.EnumerateObject()
            .Where(p => p.Name is "input_tokens" or "output_tokens" or "total_tokens" or "input_tokens_details" or "output_tokens_details" or "text_tokens" or "image_tokens" or "cached_tokens" or "reasoning_tokens")
            .ToDictionary(p => p.Name, p => NumericUsage(p.Value)),
        _ => null
    };

    private void Visit(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) Visit(item); return; }
        if (value.ValueKind != JsonValueKind.Object) return;
        var fields = new Dictionary<string, string>();
        foreach (var name in new[] { "size", "quality", "output_format" })
            if (value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
                ValidMetadata(name, field.GetString()!))
                fields[name] = field.GetString()!;
        if (fields.Count > 0) _metadata.Add(fields);
        foreach (var name in new[] { "data", "output", "result" })
            if (value.TryGetProperty(name, out var child)) Visit(child);
    }

    private static bool ValidMetadata(string name, string value)
    {
        // Preserve the former regex's optional final newline behavior without rooting the regex engine.
        value = value.EndsWith('\n') ? value[..^1] : value;
        if (name == "quality") return value is "auto" or "low" or "medium" or "high";
        if (name == "output_format") return value is "png" or "jpeg" or "webp";
        if (value == "auto") return true;
        var parts = value.Split('x');
        return parts.Length == 2 && parts.All(p => p.Length is >= 1 and <= 4 && p.All(char.IsAsciiDigit));
    }

    public string Serialize(int exit) => JsonSerializer.Serialize(new CliReportData(
        ok: exit == 0, exit_code: exit, files: Files, mode: Mode, http_status: _httpStatus,
        request_id: _requestId, elapsed_ms: _clock.ElapsedMilliseconds, usage: _usage,
        api_error: _apiError,
        error: exit == 0 ? null : Error ?? (exit == 2 ? "参数无效或无法读取输入文件；详见 stderr。" : "请求、响应或文件保存失败；详见 stderr。"),
        response_metadata: _metadata, endpoints: Endpoints,
        response_headers: _responseHeaders, request_id_status: _requestIdStatus, request_id_source: _requestIdSource,
        retry_after_seconds: RetryAfterSeconds, retry_after_source: RetryAfterSource,
        phase_elapsed_ms: Timings.ElapsedMilliseconds, request_settings: _requestSettings), JsonSerializationContext.Default.CliReportData);
}