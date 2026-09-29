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
    public string Stage { get; set; } = "prepare";
    private int? _httpStatus;
    private string? _requestId;
    private object? _usage;
    private ApiErrorReport? _apiError;
    private readonly List<Dictionary<string, string>> _metadata = [];

    public void CaptureHeaders(HttpResponseMessage response)
    {
        _httpStatus = (int)response.StatusCode;
        var retry = response.Headers.RetryAfter;
        RetryAfterSeconds = Math.Max(0, retry?.Delta?.TotalSeconds ??
            (retry?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : 0));
        foreach (var name in new[] { "x-request-id", "apim-request-id", "x-ms-request-id" })
            if (response.Headers.TryGetValues(name, out var values)) { _requestId = values.FirstOrDefault(); break; }
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
                    SafeErrorField(apiError, "code", apiKey),
                    SafeErrorField(apiError, "type", apiKey),
                    SafeErrorField(apiError, "param", apiKey),
                    "服务端返回错误。");
            Visit(document.RootElement);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("usage", out var usage))
                _usage = NumericUsage(usage);
        }
        catch (JsonException) { /* The response parser reports malformed JSON; retain HTTP metadata. */ }
    }

    internal void RedactTransportMetadata(string? apiKey)
    {
        bool Secret(string text) => !string.IsNullOrEmpty(apiKey) &&
            (text.Contains(apiKey, StringComparison.Ordinal) || text.Contains(Uri.EscapeDataString(apiKey), StringComparison.Ordinal));
        if (_requestId is { } id && (id.Length > 256 || id.Any(char.IsControl) || Secret(id))) _requestId = null;
        if (Error is { } error && Secret(error)) Error = "任务失败；敏感错误详情已隐藏。";
    }

    private static string? SafeErrorField(JsonElement error, string name, string? apiKey)
    {
        if (!error.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var value = field.GetString()!;
        // Reject rather than truncate/clean untrusted text; never copy the server's message.
        return value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '[' or ']' or '-') &&
            (string.IsNullOrEmpty(apiKey) || !value.Contains(apiKey, StringComparison.Ordinal)) ? value : null;
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
        response_metadata: _metadata, endpoints: Endpoints), JsonSerializationContext.Default.CliReportData);
}