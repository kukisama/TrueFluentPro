using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GptImageCli;

internal sealed class PhaseTimings
{
    public Dictionary<string, double?> ElapsedMilliseconds { get; } = new()
    {
        ["headers_wait"] = null, ["body_receive"] = null, ["parse"] = null,
        ["download"] = null, ["decode"] = null, ["write"] = null
    };

    public IDisposable Measure(string phase) => new Measurement(this, phase);

    private sealed class Measurement(PhaseTimings owner, string phase) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.ElapsedMilliseconds[phase] = (owner.ElapsedMilliseconds[phase] ?? 0) +
                Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        }
    }
}

// Response fields are untrusted. Messages are classified into fixed summaries, never echoed.
internal sealed class DiagnosticSanitizer
{
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    public void AddSensitiveValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _secrets.Add(value);
        _secrets.Add(Uri.EscapeDataString(value));
        _secrets.Add(Uri.EscapeDataString(Uri.EscapeDataString(value)));
        _secrets.Add(WebUtility.HtmlEncode(value));
        _secrets.Add(JsonSerializer.Serialize(value, JsonSerializationContext.Default.String)[1..^1]);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        _secrets.Add(base64);
        _secrets.Add(base64.TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    }

    public bool ContainsSensitiveValue(string value)
    {
        for (var depth = 0; depth < 3; depth++)
        {
            if (_secrets.Any(secret => value.Contains(secret, StringComparison.Ordinal))) return true;
            var decoded = WebUtility.HtmlDecode(Uri.UnescapeDataString(value));
            if (decoded == value) break;
            value = decoded;
        }
        return false;
    }

    public string? Identifier(string? value, int limit = 64, bool parameter = false)
    {
        if (string.IsNullOrEmpty(value) || value.Length > limit || ContainsSensitiveValue(value)) return null;
        if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ||
            (parameter && c is '[' or ']'))) return null;
        if (value.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase)) return null;
        return value;
    }

    public string? ErrorField(JsonElement error, string name) =>
        error.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? Identifier(field.GetString(), parameter: true) : null;

    public string ErrorSummary(JsonElement error)
    {
        var fields = new[] { "code", "type", "message" }.Select(name =>
            error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : "").Where(value => value.Length <= 8192);
        var text = string.Join(' ', fields);
        foreach (var secret in _secrets.OrderByDescending(value => value.Length))
            text = text.Replace(secret, "", StringComparison.Ordinal);
        text = text.ToLowerInvariant();
        var normalized = text.Replace('-', ' ').Replace('_', ' ');
        var words = normalized.Split([' ', ':', '.', '(', ')', ',', ';', '/', '\n', '\r', '\t'],
            StringSplitOptions.RemoveEmptyEntries);
        const string inferred = "服务端错误分类（推断）：";
        // Specific message evidence takes precedence over a generic rate_limit_exceeded code.
        if (text.Contains("concurr") || text.Contains("simultaneous") || text.Contains("并发"))
            return inferred + "并发请求数受限；请降低并发数，等待在途请求完成。";
        if (text.Contains("overload") || text.Contains("capacity") || text.Contains("server busy") || text.Contains("容量"))
            return inferred + "服务容量不足或繁忙；请降低并发或稍后重试，必要时检查部署容量。";
        if (words.Contains("tpm") || normalized.Contains("tokens per min") || normalized.Contains("token per min") ||
            normalized.Contains("tokens/min") || normalized.Contains("token/min") || text.Contains("每分钟令牌"))
            return inferred + "每分钟令牌量（TPM）受限；请降低令牌吞吐量或申请提高 TPM 配额。";
        if (words.Contains("rpm") || normalized.Contains("requests per min") || normalized.Contains("request per min") ||
            normalized.Contains("requests/min") || normalized.Contains("request/min") || text.Contains("每分钟请求"))
            return inferred + "每分钟请求数（RPM）受限；请降低请求频率或申请提高 RPM 配额。";
        if (text.Contains("quota") || text.Contains("billing") || text.Contains("credit") ||
            text.Contains("余额") || text.Contains("配额"))
            return inferred + "额度或计费限制；请检查可用额度和计费状态。";
        if (text.Contains("rate") && text.Contains("limit") || text.Contains("too many requests") ||
            text.Contains("throttl") || text.Contains("限流"))
            return inferred + "请求频率或吞吐量受限，具体限额未知；请结合限额、剩余量及重置响应头判断。";
        if (text.Contains("content_policy") || text.Contains("content policy") || text.Contains("safety"))
            return inferred + "内容安全策略限制。";
        if (text.Contains("authentication") || text.Contains("unauthorized") || text.Contains("invalid api key") ||
            text.Contains("invalid_api_key"))
            return inferred + "认证失败；请检查密钥和认证方式。";
        if (text.Contains("permission_denied") || text.Contains("forbidden"))
            return inferred + "访问被拒绝；请检查资源访问权限。";
        if (text.Contains("timeout") || text.Contains("timed out"))
            return inferred + "处理超时。";
        if (text.Contains("model_not_found") || text.Contains("deploymentnotfound"))
            return inferred + "模型或部署不可用。";
        if (text.Contains("invalid parameter") || text.Contains("unsupported") || text.Contains("invalid_value"))
            return inferred + "参数无效或不受支持。";
        if (text.Contains("server_error") || text.Contains("internal server error") || text.Contains("service_unavailable"))
            return inferred + "服务内部错误或暂不可用。";
        return "服务端返回错误。";
    }
}

internal static class DiagnosticHeaders
{
    internal static readonly string[] RequestIds =
    [
        "x-request-id", "apim-request-id", "x-ms-request-id", "request-id", "request_id", "x-requestid",
        "x-ms-correlation-request-id", "x-correlation-id", "correlation-id", "x-amzn-requestid",
        "x-amz-request-id", "cf-ray"
    ];

    internal static readonly string[] Retry = ["retry-after", "retry-after-ms", "x-ms-retry-after-ms"];
    internal static readonly string[] RateLimits =
    [
        "x-ratelimit-limit-requests", "x-ratelimit-limit-tokens", "x-ratelimit-limit-images",
        "x-rate-limit-limit-requests", "x-rate-limit-limit-tokens", "x-rate-limit-limit-images",
        "ratelimit-limit", "x-ratelimit-limit", "x-rate-limit-limit",
        "x-ratelimit-remaining-requests", "x-ratelimit-remaining-tokens", "x-ratelimit-remaining-images",
        "x-ratelimit-reset-requests", "x-ratelimit-reset-tokens", "x-ratelimit-reset-images",
        "x-rate-limit-remaining-requests", "x-rate-limit-remaining-tokens", "x-rate-limit-remaining-images",
        "x-rate-limit-reset-requests", "x-rate-limit-reset-tokens", "x-rate-limit-reset-images",
        "ratelimit-remaining", "ratelimit-reset", "x-ratelimit-remaining", "x-ratelimit-reset",
        "x-rate-limit-remaining", "x-rate-limit-reset"
    ];

    internal static bool Number(string value, out double number)
    {
        number = 0;
        return value.Length is > 0 and <= 24 && value.All(c => char.IsAsciiDigit(c) || c == '.') &&
            double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number) && number >= 0;
    }

    internal static bool RetryValue(string name, string value, out double seconds)
    {
        seconds = 0;
        if (value.Length is > 24 and <= 8192 && value.All(char.IsAsciiDigit))
        {
            value = value.TrimStart('0');
            if (value.Length == 0) value = "0";
            if (value.Length > 24)
            {
                // Retain an excessive hint without overflowing a scheduler's numeric representation.
                seconds = TimeSpan.FromDays(366).TotalSeconds;
                return true;
            }
        }
        if (Number(value, out var number))
        {
            seconds = name.EndsWith("-ms", StringComparison.Ordinal) ? number / 1000 : number;
            return true;
        }
        if (name == "retry-after" && DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var date))
        {
            seconds = Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds);
            return true;
        }
        return false;
    }

    internal static bool RateLimitValue(string name, string value)
    {
        if (Number(value, out _)) return true;
        if (!name.Contains("reset", StringComparison.Ordinal) || value.Length is 0 or > 64) return false;
        // OpenAI reset durations are composed of numeric units, for example 1m30s or 250ms.
        var index = 0;
        while (index < value.Length)
        {
            var start = index;
            while (index < value.Length && (char.IsAsciiDigit(value[index]) || value[index] == '.')) index++;
            if (!Number(value[start..index], out _) || index == value.Length) return false;
            var unit = value[index++];
            if (unit == 'm' && index < value.Length && value[index] == 's') index++;
            else if (unit is not ('d' or 'h' or 'm' or 's')) return false;
        }
        return true;
    }
}
