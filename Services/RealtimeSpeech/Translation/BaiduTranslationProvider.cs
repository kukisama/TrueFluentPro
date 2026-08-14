using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.RealtimeSpeech.Translation
{
    /// <summary>
    /// 百度翻译 Provider（fanyi.baidu.com 开放平台）。两条路径：
    ///   · 大模型文本翻译：Authorization: Bearer {翻译 ApiKey}（含免费额度，优先）；
    ///   · 通用文本翻译：sign = MD5(appid+q+salt+翻译 SecretKey)（回退）。
    /// 凭据来自资源的 Translate* 字段，与 ASR 凭据相互独立。
    /// </summary>
    public sealed class BaiduTranslationProvider : ITranslationProvider
    {
        private const string TranslateUrl = "https://fanyi-api.baidu.com/api/trans/vip/translate";
        private const string LlmTranslateUrl = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";

        private static readonly HttpClient HttpClient = new();
        private static readonly Random Rng = new();

        private readonly string _appId;
        private readonly string _llmApiKey;
        private readonly string _generalSecret;
        private readonly Action<string>? _status;
        private readonly Action<string>? _log;

        public BaiduTranslationProvider(
            string? translateAppId,
            string? fallbackAppId,
            string? translateApiKey,
            string? translateSecret,
            Action<string>? status = null,
            Action<string>? log = null)
        {
            _appId = string.IsNullOrWhiteSpace(translateAppId) ? (fallbackAppId?.Trim() ?? string.Empty) : translateAppId.Trim();
            _llmApiKey = translateApiKey?.Trim() ?? string.Empty;
            _generalSecret = translateSecret?.Trim() ?? string.Empty;
            _status = status;
            _log = log;
        }

        public string DisplayName => "百度翻译";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_llmApiKey) || !string.IsNullOrWhiteSpace(_generalSecret);

        public async Task<string> TranslateAsync(string sourceText, string? sourceLanguage, string? targetLanguage, CancellationToken cancellationToken)
        {
            var from = MapTranslateLanguage(sourceLanguage, isSource: true);
            var to = MapTranslateLanguage(targetLanguage, isSource: false);

            // 路径一：大模型文本翻译（含免费额度，优先）。需要 翻译 ApiKey + 翻译 AppId。
            if (!string.IsNullOrWhiteSpace(_llmApiKey))
            {
                if (string.IsNullOrWhiteSpace(_appId))
                {
                    _log?.Invoke("大模型翻译已配置 ApiKey 但缺少翻译 AppId，跳过该路径");
                }
                else
                {
                    var llm = await TranslateViaLlmAsync(_appId, _llmApiKey, from, to, sourceText, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(llm))
                    {
                        return llm;
                    }

                    _log?.Invoke("大模型翻译未返回译文，尝试通用翻译回退");
                }
            }

            // 路径二：通用文本翻译（sign 鉴权）。需要 翻译 AppId + 翻译 SecretKey。
            if (!string.IsNullOrWhiteSpace(_appId) && !string.IsNullOrWhiteSpace(_generalSecret))
            {
                var general = await TranslateViaSignAsync(_appId, _generalSecret, from, to, sourceText, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(general))
                {
                    return general;
                }
            }

            if (string.IsNullOrWhiteSpace(_llmApiKey) && string.IsNullOrWhiteSpace(_generalSecret))
            {
                _status?.Invoke("百度翻译未配置翻译凭据（翻译 ApiKey 或 SecretKey），仅显示原文。");
            }

            return string.Empty;
        }

        private async Task<string?> TranslateViaLlmAsync(string appId, string apiKey, string from, string to, string sourceText, CancellationToken cancellationToken)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new { appid = appId, from, to, q = sourceText });
                using var request = new HttpRequestMessage(HttpMethod.Post, LlmTranslateUrl)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

                using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                if (root.TryGetProperty("error_code", out var errCodeEl))
                {
                    var code = errCodeEl.ValueKind == JsonValueKind.String ? errCodeEl.GetString() : errCodeEl.GetRawText();
                    if (!string.IsNullOrWhiteSpace(code) && code != "0" && code != "\"0\"" && code != "52000")
                    {
                        var errMsg = root.TryGetProperty("error_msg", out var errMsgEl) ? errMsgEl.GetString() : "未知错误";
                        _log?.Invoke($"大模型翻译失败 error_code={code} error_msg={errMsg}");
                        _status?.Invoke($"百度大模型翻译失败(error_code={code}): {errMsg}");
                        return null;
                    }
                }

                return ExtractTransResult(root);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"大模型翻译异常 {ex.Message}");
                _status?.Invoke($"百度大模型翻译异常: {ex.Message}");
                return null;
            }
        }

        private async Task<string?> TranslateViaSignAsync(string appId, string secretKey, string from, string to, string sourceText, CancellationToken cancellationToken)
        {
            try
            {
                var salt = Rng.Next(100000, 999999).ToString();
                var sign = Md5Hex(appId + sourceText + salt + secretKey);

                var form = new Dictionary<string, string>
                {
                    ["q"] = sourceText,
                    ["from"] = from,
                    ["to"] = to,
                    ["appid"] = appId,
                    ["salt"] = salt,
                    ["sign"] = sign
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, TranslateUrl)
                {
                    Content = new FormUrlEncodedContent(form)
                };

                using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(responseText);
                var root = doc.RootElement;

                if (root.TryGetProperty("error_code", out var errCodeEl))
                {
                    var errMsg = root.TryGetProperty("error_msg", out var errMsgEl) ? errMsgEl.GetString() : "未知错误";
                    var code = errCodeEl.ValueKind == JsonValueKind.String ? errCodeEl.GetString() : errCodeEl.GetInt32().ToString();
                    _log?.Invoke($"通用翻译失败 error_code={code} error_msg={errMsg}");
                    _status?.Invoke($"百度通用翻译失败(error_code={code}): {errMsg}");
                    return null;
                }

                return ExtractTransResult(root);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"通用翻译异常 {ex.Message}");
                _status?.Invoke($"百度通用翻译异常: {ex.Message}");
                return null;
            }
        }

        private static string? ExtractTransResult(JsonElement root)
        {
            if (root.TryGetProperty("trans_result", out var transEl)
                && transEl.ValueKind == JsonValueKind.Array
                && transEl.GetArrayLength() > 0)
            {
                var sb = new StringBuilder();
                foreach (var item in transEl.EnumerateArray())
                {
                    if (item.TryGetProperty("dst", out var dstEl) && dstEl.ValueKind == JsonValueKind.String)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }

                        sb.Append(dstEl.GetString());
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }

            return null;
        }

        /// <summary>百度通用翻译语种码（中文=zh，日语=jp，韩语=kor，法语=fra）。</summary>
        private static string MapTranslateLanguage(string? language, bool isSource)
        {
            if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return isSource ? "auto" : "en";
            }

            var lower = language.ToLowerInvariant();
            return lower switch
            {
                _ when lower.StartsWith("zh") => "zh",
                _ when lower.StartsWith("en") => "en",
                _ when lower.StartsWith("ja") => "jp",
                _ when lower.StartsWith("ko") => "kor",
                _ when lower.StartsWith("fr") => "fra",
                _ when lower.StartsWith("de") => "de",
                _ when lower.StartsWith("es") => "spa",
                _ when lower.StartsWith("ru") => "ru",
                _ when lower.StartsWith("it") => "it",
                _ when lower.StartsWith("vi") => "vie",
                _ => "en"
            };
        }

        private static string Md5Hex(string input)
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
