using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GptImageCli;

internal sealed class CliReport
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public string? Mode { get; set; }
    public string? Error { get; set; }
    public EndpointSummary[]? Endpoints { get; set; }
    public List<string> Files { get; } = [];
    private int? _httpStatus;
    private string? _requestId;
    private object? _usage;
    private ApiErrorReport? _apiError;
    private readonly List<Dictionary<string, string>> _metadata = [];

    public void CaptureHeaders(HttpResponseMessage response)
    {
        _httpStatus = (int)response.StatusCode;
        foreach (var name in new[] { "x-request-id", "apim-request-id", "x-ms-request-id" })
            if (response.Headers.TryGetValues(name, out var values)) { _requestId = values.FirstOrDefault(); break; }
    }

    public void CaptureBody(string text, string? apiKey = null)
    {
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

    private static string? SafeErrorField(JsonElement error, string name, string? apiKey)
    {
        if (!error.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return null;
        var value = field.GetString()!;
        // Reject rather than truncate/clean untrusted text; never copy the server's message.
        return value.Length is > 0 and <= 64 && Regex.IsMatch(value, @"\A[A-Za-z0-9_.\[\]-]+\z") &&
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
                Regex.IsMatch(field.GetString()!, name == "size" ? "^(auto|[0-9]{1,4}x[0-9]{1,4})$" : name == "quality" ? "^(auto|low|medium|high)$" : "^(png|jpeg|webp)$"))
                fields[name] = field.GetString()!;
        if (fields.Count > 0) _metadata.Add(fields);
        foreach (var name in new[] { "data", "output", "result" })
            if (value.TryGetProperty(name, out var child)) Visit(child);
    }

    public string Serialize(int exit) => JsonSerializer.Serialize(new CliReportData(
        ok: exit == 0, exit_code: exit, files: Files, mode: Mode, http_status: _httpStatus,
        request_id: _requestId, elapsed_ms: _clock.ElapsedMilliseconds, usage: _usage,
        api_error: _apiError,
        error: exit == 0 ? null : Error ?? (exit == 2 ? "参数无效或无法读取输入文件；详见 stderr。" : "请求、响应或文件保存失败；详见 stderr。"),
        response_metadata: _metadata, endpoints: Endpoints), JsonSerializationContext.Default.CliReportData);
}