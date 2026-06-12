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
    /// 百度实时翻译服务：实时语音识别(百度智能云 BCE) → 文本翻译(百度翻译开放平台)。
    /// 注意：识别与翻译是两个不同平台、不同凭据：
    ///   · 识别：BCE 控制台的 AppId(数字) + API Key（START 帧 appkey）。
    ///   · 翻译：fanyi.baidu.com 开放平台，支持两种鉴权：
    ///       - 大模型文本翻译：Bearer = 翻译 ApiKey（含免费额度，优先）；
    ///       - 通用文本翻译：sign = MD5(appid+q+salt+密钥)，密钥 = 翻译 SecretKey。
    /// </summary>
    public sealed class BaiduRealtimeTranslationService : CascadedRealtimeTranslationServiceBase
    {
        private const string TranslateUrl = "https://fanyi-api.baidu.com/api/trans/vip/translate";
        private const string LlmTranslateUrl = "https://fanyi-api.baidu.com/ait/api/aiTextTranslate";
        private const int ChunkSize = 5120; // 16k 16bit 单声道 160ms = 5120 字节

        private static readonly HttpClient HttpClient = new();
        private static readonly Random Rng = new();

        // 实时识别建议每 160ms 发一块，需对采集到的块做重切分缓冲。
        private readonly MemoryStream _frameBuffer = new();

        public BaiduRealtimeTranslationService(
            AzureSpeechConfig config,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            Action<string>? auditLog = null)
            : base(config, speechResourceRuntimeResolver, auditLog)
        {
        }

        public override RealtimeConnectorFamily ConnectorFamily => RealtimeConnectorFamily.BaiduRealtimeAsr;

        protected override int AsrSampleRate => 16000;

        protected override string VendorDisplayName => "百度";

        protected override Uri BuildWebSocketUri(SpeechResource resource)
        {
            var sn = BuildSerialNumber();
            return new Uri($"wss://vop.baidu.com/realtime_asr?sn={sn}");
        }

        protected override async Task OnConnectedAsync(SpeechResource resource, CancellationToken cancellationToken)
        {
            var appId = ParseAppId(resource.AppId);
            var devPid = MapDevPid(_config.SourceLanguage);
            LogDebug($"发送 START 帧 appid={appId} appkey={MaskCredential(resource.ApiKey)} dev_pid={devPid} 源语言={_config.SourceLanguage ?? "(空)"}");

            var startPayload = new
            {
                type = "START",
                data = new
                {
                    appid = appId,
                    appkey = resource.ApiKey.Trim(),
                    dev_pid = devPid,
                    cuid = "TrueFluentPro",
                    sample = 16000,
                    format = "pcm"
                }
            };

            await SendTextAsync(JsonSerializer.Serialize(startPayload), cancellationToken).ConfigureAwait(false);
        }

        protected override async Task OnAudioChunkAsync(SpeechResource resource, byte[] pcm16, CancellationToken cancellationToken)
        {
            _frameBuffer.Write(pcm16, 0, pcm16.Length);
            await FlushFramesAsync(false, cancellationToken).ConfigureAwait(false);
        }

        protected override async Task OnAudioCompletedAsync(SpeechResource resource, CancellationToken cancellationToken)
        {
            await FlushFramesAsync(true, cancellationToken).ConfigureAwait(false);
            await SendTextAsync("{\"type\":\"FINISH\"}", cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushFramesAsync(bool flushRemainder, CancellationToken cancellationToken)
        {
            var data = _frameBuffer.ToArray();
            var offset = 0;
            while (data.Length - offset >= ChunkSize)
            {
                var frame = new byte[ChunkSize];
                Array.Copy(data, offset, frame, 0, ChunkSize);
                await SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
                offset += ChunkSize;
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

            // 错误码
            if (root.TryGetProperty("err_no", out var errNoEl)
                && errNoEl.ValueKind == JsonValueKind.Number
                && errNoEl.GetInt32() != 0)
            {
                var errMsg = root.TryGetProperty("err_msg", out var errMsgEl) ? errMsgEl.GetString() : "未知错误";
                var errNo = errNoEl.GetInt32();
                LogDebug($"识别错误 err_no={errNo} err_msg={errMsg} 原始={Truncate(json, 300)}");
                RaiseStatus($"百度实时语音错误(err_no={errNo}): {errMsg}{DescribeAsrError(errNo)}");
                return;
            }

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var result = root.TryGetProperty("result", out var resultEl) ? resultEl.GetString() : null;

            switch (type)
            {
                case "MID_TEXT":
                    LogDebug($"中间识别(MID_TEXT): {Truncate(result, 120)}");
                    ReportPartialOriginal(result);
                    break;
                case "FIN_TEXT":
                    LogDebug($"定稿识别(FIN_TEXT): {Truncate(result, 120)}");
                    ReportFinalOriginal(result);
                    break;
                case "HEARTBEAT":
                    break;
                default:
                    LogDebug($"其他消息 type={type ?? "(无)"} 原始={Truncate(json, 200)}");
                    break;
            }
        }

        /// <summary>探活时解析百度首帧：err_no!=0 视为握手失败。</summary>
        protected override bool IsProbeResponseSuccess(string message, out string detail)
        {
            detail = string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                if (root.TryGetProperty("err_no", out var errNoEl)
                    && errNoEl.ValueKind == JsonValueKind.Number
                    && errNoEl.GetInt32() != 0)
                {
                    var errMsg = root.TryGetProperty("err_msg", out var errMsgEl) ? errMsgEl.GetString() : "未知错误";
                    detail = $"err_no={errNoEl.GetInt32()} {errMsg}{DescribeAsrError(errNoEl.GetInt32())}";
                    return false;
                }
            }
            catch
            {
                // 非 JSON 首帧不视为失败，交由上层按“收到响应”处理。
            }

            return true;
        }

        protected override async Task<string> TranslateAsync(SpeechResource resource, string sourceText, CancellationToken cancellationToken)
        {
            // 翻译走「百度翻译开放平台」(fanyi.baidu.com)，与识别(BCE)凭据相互独立。
            var translateAppId = string.IsNullOrWhiteSpace(resource.TranslateAppId)
                ? resource.AppId?.Trim() ?? string.Empty
                : resource.TranslateAppId.Trim();
            var llmApiKey = resource.TranslateApiKey?.Trim() ?? string.Empty;   // 大模型文本翻译 Bearer
            var generalSecret = resource.TranslateApiSecret?.Trim() ?? string.Empty; // 通用文本翻译密钥

            var from = MapTranslateLanguage(_config.SourceLanguage, isSource: true);
            var to = MapTranslateLanguage(_config.TargetLanguage, isSource: false);

            // 路径一：大模型文本翻译（含免费额度，优先）。需要 翻译 ApiKey + 翻译 AppId。
            if (!string.IsNullOrWhiteSpace(llmApiKey))
            {
                if (string.IsNullOrWhiteSpace(translateAppId))
                {
                    LogDebug("大模型翻译已配置 ApiKey 但缺少翻译 AppId，跳过该路径");
                }
                else
                {
                    LogDebug($"大模型翻译请求 appid={MaskCredential(translateAppId)} apikey={MaskCredential(llmApiKey)} from={from} to={to} q={Truncate(sourceText, 80)}");
                    var llm = await TranslateViaLlmAsync(translateAppId, llmApiKey, from, to, sourceText, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(llm))
                    {
                        LogDebug($"大模型翻译成功 dst={Truncate(llm, 80)}");
                        return llm;
                    }

                    LogDebug("大模型翻译未返回译文，尝试通用翻译回退");
                }
            }

            // 路径二：通用文本翻译（sign 鉴权）。需要 翻译 AppId + 翻译 SecretKey。
            if (!string.IsNullOrWhiteSpace(translateAppId) && !string.IsNullOrWhiteSpace(generalSecret))
            {
                LogDebug($"通用翻译请求 appid={MaskCredential(translateAppId)} secret={MaskCredential(generalSecret)} from={from} to={to} q={Truncate(sourceText, 80)}");
                var general = await TranslateViaSignAsync(translateAppId, generalSecret, from, to, sourceText, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(general))
                {
                    LogDebug($"通用翻译成功 dst={Truncate(general, 80)}");
                    return general;
                }

                LogDebug("通用翻译未返回译文");
            }

            if (string.IsNullOrWhiteSpace(llmApiKey) && string.IsNullOrWhiteSpace(generalSecret))
            {
                LogDebug("未配置任何翻译凭据（翻译 ApiKey / 翻译 SecretKey 均为空），仅显示原文");
                RaiseStatus("百度翻译未配置翻译凭据（翻译 ApiKey 或 SecretKey），仅显示原文。");
            }

            return sourceText;
        }

        /// <summary>大模型文本翻译：Authorization: Bearer {apiKey}，JSON 请求。</summary>
        private async Task<string?> TranslateViaLlmAsync(
            string appId, string apiKey, string from, string to, string sourceText, CancellationToken cancellationToken)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    appid = appId,
                    from,
                    to,
                    q = sourceText
                });

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
                        LogDebug($"大模型翻译失败 error_code={code} error_msg={errMsg}");
                        RaiseStatus($"百度大模型翻译失败(error_code={code}): {errMsg}");
                        return null;
                    }
                }

                return ExtractTransResult(root);
            }
            catch (Exception ex)
            {
                LogDebug($"大模型翻译异常 {ex.Message}");
                RaiseStatus($"百度大模型翻译异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>通用文本翻译：sign = MD5(appid+q+salt+密钥)，表单请求。</summary>
        private async Task<string?> TranslateViaSignAsync(
            string appId, string secretKey, string from, string to, string sourceText, CancellationToken cancellationToken)
        {
            try
            {
                var salt = Rng.Next(100000, 999999).ToString();
                // sign = MD5(appid + q + salt + secretKey)，q 在拼接 sign 之前不做 urlencode
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
                    LogDebug($"通用翻译失败 error_code={code} error_msg={errMsg}");
                    RaiseStatus($"百度通用翻译失败(error_code={code}): {errMsg}");
                    return null;
                }

                return ExtractTransResult(root);
            }
            catch (Exception ex)
            {
                LogDebug($"通用翻译异常 {ex.Message}");
                RaiseStatus($"百度通用翻译异常: {ex.Message}");
                return null;
            }
        }

        /// <summary>从响应中提取 trans_result[].dst 拼接。</summary>
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

        /// <summary>
        /// 按源语言映射百度实时识别 dev_pid。取值依据官方文档（开放平台模型，加强标点）：
        /// 15372=中文普通话，17372=英语。
        /// 见 https://cloud.baidu.com/doc/SPEECH/s/jlbxejt2i
        /// </summary>
        private static int MapDevPid(string? sourceLanguage)
        {
            if (string.IsNullOrWhiteSpace(sourceLanguage))
            {
                return 15372;
            }

            var lower = sourceLanguage.ToLowerInvariant();
            return lower switch
            {
                _ when lower.StartsWith("en") => 17372, // 英语（加强标点）
                _ when lower.StartsWith("zh") => 15372, // 中文普通话（加强标点）
                _ => 15372
            };
        }

        /// <summary>为常见的百度实时识别错误码补充可操作的中文说明。</summary>
        private static string DescribeAsrError(int errNo)
        {
            return errNo switch
            {
                -3004 or 3004 =>
                    "（鉴权失败/无权限：请确认该应用已在百度智能云语音控制台勾选并开通“实时语音识别”接口——该接口需单独开通付费，与短语音识别、文本翻译是不同产品）",
                -3005 or 3005 => "（无权限：appid 与 appkey 不匹配，请核对应用信息）",
                _ => string.Empty
            };
        }

        private static string BuildSerialNumber()
        {
            var bytes = new byte[8];
            RandomNumberGenerator.Fill(bytes);
            var sb = new StringBuilder(16);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        private static int ParseAppId(string? appId)
            => int.TryParse((appId ?? string.Empty).Trim(), out var value) ? value : 0;

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
    }
}
