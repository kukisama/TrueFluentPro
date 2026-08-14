using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.RealtimeSpeech.Translation
{
    /// <summary>
    /// 讯飞 NiuTrans 机器翻译 Provider（ntrans.xfyun.cn/v2/ots）。
    /// HmacSHA256 签名鉴权；凭据来自资源的 Translate* 字段（AppId/ApiKey/ApiSecret），与 ASR 凭据独立。
    /// </summary>
    public sealed class XunfeiTranslationProvider : ITranslationProvider
    {
        private const string NiuTransUrl = "https://ntrans.xfyun.cn/v2/ots";
        private const string NiuTransHost = "ntrans.xfyun.cn";

        private static readonly HttpClient HttpClient = new();

        private readonly string _appId;
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly Action<string>? _status;
        private readonly Action<string>? _log;

        public XunfeiTranslationProvider(
            string? translateAppId,
            string? fallbackAppId,
            string? translateApiKey,
            string? fallbackApiKey,
            string? translateApiSecret,
            Action<string>? status = null,
            Action<string>? log = null)
        {
            _appId = string.IsNullOrWhiteSpace(translateAppId) ? (fallbackAppId?.Trim() ?? string.Empty) : translateAppId.Trim();
            _apiKey = string.IsNullOrWhiteSpace(translateApiKey) ? (fallbackApiKey?.Trim() ?? string.Empty) : translateApiKey.Trim();
            _apiSecret = translateApiSecret?.Trim() ?? string.Empty;
            _status = status;
            _log = log;
        }

        public string DisplayName => "讯飞 NiuTrans";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_appId)
                                    && !string.IsNullOrWhiteSpace(_apiKey)
                                    && !string.IsNullOrWhiteSpace(_apiSecret);

        public async Task<string> TranslateAsync(string sourceText, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken)
        {
            if (!IsConfigured)
            {
                _status?.Invoke("讯飞 NiuTrans 缺少翻译凭据(AppId/ApiKey/ApiSecret)，仅显示原文。");
                return string.Empty;
            }

            var from = MapNiuTransLanguage(sourceLanguage, isSource: true);
            var to = MapNiuTransLanguage(targetLanguage, isSource: false);

            try
            {
                var text = sourceText.Length > 5000 ? sourceText[..5000] : sourceText;
                var bodyObj = new
                {
                    common = new { app_id = _appId },
                    business = new { from, to },
                    data = new { text = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }
                };
                var body = JsonSerializer.Serialize(bodyObj);

                var date = DateTime.UtcNow.ToString("R"); // RFC1123 GMT
                var digest = "SHA-256=" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
                var sigOrigin = $"host: {NiuTransHost}\ndate: {date}\nPOST /v2/ots HTTP/1.1\ndigest: {digest}";
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
                var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(sigOrigin)));
                var authorization = $"api_key=\"{_apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line digest\", signature=\"{signature}\"";

                using var request = new HttpRequestMessage(HttpMethod.Post, NiuTransUrl);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Host", NiuTransHost);
                request.Headers.TryAddWithoutValidation("Date", date);
                request.Headers.TryAddWithoutValidation("Digest", digest);
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

                using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;
                var code = root.TryGetProperty("code", out var codeEl)
                    ? (codeEl.ValueKind == JsonValueKind.Number ? codeEl.GetInt32().ToString() : codeEl.GetString())
                    : null;
                if (code != "0")
                {
                    var message = root.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "未知错误";
                    _log?.Invoke($"NiuTrans 翻译失败 code={code} message={message}");
                    _status?.Invoke($"讯飞 NiuTrans 翻译失败(code={code}): {message}");
                    return string.Empty;
                }

                if (root.TryGetProperty("data", out var dataEl)
                    && dataEl.TryGetProperty("result", out var resultEl)
                    && resultEl.TryGetProperty("trans_result", out var transEl)
                    && transEl.TryGetProperty("dst", out var dstEl)
                    && dstEl.ValueKind == JsonValueKind.String)
                {
                    return dstEl.GetString() ?? string.Empty;
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"NiuTrans 翻译异常 {ex.Message}");
                _status?.Invoke($"讯飞 NiuTrans 翻译异常: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>NiuTrans 语种码（中文=cn 非 zh）。</summary>
        private static string MapNiuTransLanguage(string? language, bool isSource)
        {
            if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return isSource ? "cn" : "en";
            }

            var lower = language.ToLowerInvariant();
            return lower switch
            {
                _ when lower.StartsWith("zh") => "cn",
                _ when lower.StartsWith("en") => "en",
                _ when lower.StartsWith("ja") => "ja",
                _ when lower.StartsWith("ko") => "ko",
                _ when lower.StartsWith("fr") => "fr",
                _ when lower.StartsWith("de") => "de",
                _ when lower.StartsWith("es") => "es",
                _ when lower.StartsWith("ru") => "ru",
                _ when lower.StartsWith("it") => "it",
                _ when lower.StartsWith("vi") => "vi",
                _ => "en"
            };
        }
    }
}
