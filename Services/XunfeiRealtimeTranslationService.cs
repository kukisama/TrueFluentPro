using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 讯飞实时翻译服务：RTASR(实时语音转写) → NiuTrans(机器翻译)。
    /// 协议参考讯飞官方文档，凭据为 AppId + ApiKey(RTASR) + 翻译 AppId/ApiKey/ApiSecret(NiuTrans)。
    /// </summary>
    public sealed class XunfeiRealtimeTranslationService : CascadedRealtimeTranslationServiceBase
    {
        private const string RtasrHost = "rtasr.xfyun.cn";
        private const string NiuTransUrl = "https://ntrans.xfyun.cn/v2/ots";
        private const string NiuTransHost = "ntrans.xfyun.cn";
        private const int FrameSize = 1280; // 16k 16bit 单声道 40ms = 1280 字节

        private static readonly HttpClient HttpClient = new();

        // RTASR 要求按固定 1280 字节切帧发送，需对采集到的块做重切分缓冲。
        private readonly MemoryStream _frameBuffer = new();

        public XunfeiRealtimeTranslationService(
            AzureSpeechConfig config,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            Action<string>? auditLog = null)
            : base(config, speechResourceRuntimeResolver, auditLog)
        {
        }

        public override RealtimeConnectorFamily ConnectorFamily => RealtimeConnectorFamily.XunfeiRtasr;

        protected override int AsrSampleRate => 16000;

        protected override string VendorDisplayName => "讯飞";

        protected override Uri BuildWebSocketUri(SpeechResource resource)
        {
            var appId = resource.AppId.Trim();
            var apiKey = resource.ApiKey.Trim();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // baseString = MD5(appId + ts); signa = base64(HmacSHA1(baseString, apiKey))
            var md5 = Md5Hex(appId + ts);
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(apiKey));
            var signa = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(md5)));

            var lang = MapRtasrLanguage(_config.SourceLanguage);
            var sb = new StringBuilder($"wss://{RtasrHost}/v1/ws?");
            sb.Append("appid=").Append(Uri.EscapeDataString(appId));
            sb.Append("&ts=").Append(Uri.EscapeDataString(ts));
            sb.Append("&signa=").Append(Uri.EscapeDataString(signa));
            if (!string.IsNullOrEmpty(lang))
            {
                sb.Append("&lang=").Append(Uri.EscapeDataString(lang));
            }

            LogDebug($"构建 RTASR 连接 appid={MaskCredential(appId)} apikey={MaskCredential(apiKey)} ts={ts} lang={lang} 源语言={_config.SourceLanguage ?? "(空)"}");
            return new Uri(sb.ToString());
        }

        protected override async Task OnAudioChunkAsync(SpeechResource resource, byte[] pcm16, CancellationToken cancellationToken)
        {
            _frameBuffer.Write(pcm16, 0, pcm16.Length);
            await FlushFramesAsync(false, cancellationToken).ConfigureAwait(false);
        }

        protected override async Task OnAudioCompletedAsync(SpeechResource resource, CancellationToken cancellationToken)
        {
            // 发送残留不足一帧的数据，再发送结束标记。
            await FlushFramesAsync(true, cancellationToken).ConfigureAwait(false);
            await SendTextAsync("{\"end\": true}", cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushFramesAsync(bool flushRemainder, CancellationToken cancellationToken)
        {
            var data = _frameBuffer.ToArray();
            var offset = 0;
            while (data.Length - offset >= FrameSize)
            {
                var frame = new byte[FrameSize];
                Array.Copy(data, offset, frame, 0, FrameSize);
                await SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
                offset += FrameSize;
            }

            var remaining = data.Length - offset;
            _frameBuffer.SetLength(0);
            if (flushRemainder)
            {
                if (remaining > 0)
                {
                    var tail = new byte[remaining];
                    Array.Copy(data, offset, tail, 0, remaining);
                    await SendBinaryAsync(tail, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (remaining > 0)
            {
                _frameBuffer.Write(data, offset, remaining);
            }
        }

        protected override void HandleTextMessage(SpeechResource resource, string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
            if (string.Equals(action, "error", StringComparison.OrdinalIgnoreCase))
            {
                var desc = root.TryGetProperty("desc", out var descEl) ? descEl.GetString() : "未知错误";
                var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                LogDebug($"RTASR 错误 action=error code={code} desc={desc} 原始={Truncate(json, 300)}");
                RaiseStatus($"讯飞 RTASR 错误: {desc}");
                return;
            }

            if (!string.Equals(action, "result", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(action, "started", StringComparison.OrdinalIgnoreCase))
                {
                    LogDebug("RTASR 握手成功 action=started");
                }

                return; // started / 心跳等忽略
            }

            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var inner = dataEl.GetString();
            if (string.IsNullOrWhiteSpace(inner))
            {
                return;
            }

            ParseRtasrResult(inner);
        }

        private void ParseRtasrResult(string inner)
        {
            using var doc = JsonDocument.Parse(inner);
            if (!doc.RootElement.TryGetProperty("cn", out var cn)
                || !cn.TryGetProperty("st", out var st))
            {
                return;
            }

            var type = st.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "1";
            var text = ExtractWords(st);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // type "0" = 最终结果(定稿)，"1" = 中间结果
            if (type == "0")
            {
                LogDebug($"定稿识别(type=0): {Truncate(text, 120)}");
                ReportFinalOriginal(text);
            }
            else
            {
                LogDebug($"中间识别(type=1): {Truncate(text, 120)}");
                ReportPartialOriginal(text);
            }
        }

        private static string ExtractWords(JsonElement st)
        {
            if (!st.TryGetProperty("rt", out var rt) || rt.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var rtItem in rt.EnumerateArray())
            {
                if (!rtItem.TryGetProperty("ws", out var ws) || ws.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var wsItem in ws.EnumerateArray())
                {
                    if (!wsItem.TryGetProperty("cw", out var cw) || cw.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var cwItem in cw.EnumerateArray())
                    {
                        if (cwItem.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.String)
                        {
                            sb.Append(w.GetString());
                        }
                    }
                }
            }

            return sb.ToString();
        }

        protected override async Task<string> TranslateAsync(SpeechResource resource, string sourceText, CancellationToken cancellationToken)
        {
            var appId = string.IsNullOrWhiteSpace(resource.TranslateAppId) ? resource.AppId.Trim() : resource.TranslateAppId.Trim();
            var apiKey = string.IsNullOrWhiteSpace(resource.TranslateApiKey) ? resource.ApiKey.Trim() : resource.TranslateApiKey.Trim();
            var apiSecret = resource.TranslateApiSecret.Trim();

            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret))
            {
                LogDebug($"NiuTrans 翻译凭据不全(appId={MaskCredential(appId)} apiKey={MaskCredential(apiKey)} apiSecret={MaskCredential(apiSecret)})，仅显示原文");
                RaiseStatus("讯飞 NiuTrans 缺少翻译凭据(AppId/ApiKey/ApiSecret)，仅显示原文。");
                return sourceText;
            }

            var from = MapNiuTransLanguage(_config.SourceLanguage, isSource: true);
            var to = MapNiuTransLanguage(_config.TargetLanguage, isSource: false);
            LogDebug($"NiuTrans 翻译请求 appid={MaskCredential(appId)} from={from} to={to} q={Truncate(sourceText, 80)}");

            var text = sourceText.Length > 5000 ? sourceText[..5000] : sourceText;
            var bodyObj = new
            {
                common = new { app_id = appId },
                business = new { from, to },
                data = new { text = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) }
            };
            var body = JsonSerializer.Serialize(bodyObj);

            var date = DateTime.UtcNow.ToString("R"); // RFC1123 GMT
            var digest = "SHA-256=" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
            var sigOrigin = $"host: {NiuTransHost}\ndate: {date}\nPOST /v2/ots HTTP/1.1\ndigest: {digest}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
            var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(sigOrigin)));
            var authorization = $"api_key=\"{apiKey}\", algorithm=\"hmac-sha256\", headers=\"host date request-line digest\", signature=\"{signature}\"";

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
                LogDebug($"NiuTrans 翻译失败 code={code} message={message}");
                RaiseStatus($"讯飞 NiuTrans 翻译失败(code={code}): {message}");
                return sourceText;
            }

            if (root.TryGetProperty("data", out var dataEl)
                && dataEl.TryGetProperty("result", out var resultEl)
                && resultEl.TryGetProperty("trans_result", out var transEl)
                && transEl.TryGetProperty("dst", out var dstEl)
                && dstEl.ValueKind == JsonValueKind.String)
            {
                var dst = dstEl.GetString();
                LogDebug($"NiuTrans 翻译成功 dst={Truncate(dst, 80)}");
                return dst ?? sourceText;
            }

            return sourceText;
        }

        /// <summary>探活时解析讯飞首帧：action=error 视为握手失败。</summary>
        protected override bool IsProbeResponseSuccess(string message, out string detail)
        {
            detail = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
                if (string.Equals(action, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var desc = root.TryGetProperty("desc", out var descEl) ? descEl.GetString() : "未知错误";
                    var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                    detail = code != null ? $"code={code} {desc}" : desc ?? "未知错误";
                    return false;
                }
            }
            catch
            {
                // 非 JSON 首帧不视为失败。
            }

            return true;
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

        /// <summary>RTASR 语种：仅支持中文(cn)/英文(en)，默认中文。</summary>
        private static string MapRtasrLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language) || language.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                return "cn";
            }

            return language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "cn";
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
