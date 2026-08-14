using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.RealtimeSpeech.Asr
{
    /// <summary>
    /// 讯飞实时语音转写（RTASR）连接器。协议：wss://rtasr.xfyun.cn/v1/ws。
    /// 鉴权 signa=base64(HmacSHA1(MD5(appid+ts), apiKey))；音频按 1280 字节(40ms)切帧；收尾发 {"end":true}。
    /// 仅封装识别协议；翻译由独立的 ITranslationProvider 负责。
    /// </summary>
    public sealed class XunfeiAsrConnector : IAsrConnector
    {
        private const string RtasrHost = "rtasr.xfyun.cn";
        private const int FrameSize = 1280; // 16k 16bit 单声道 40ms = 1280 字节

        // RTASR 要求按固定 1280 字节切帧发送，需对采集到的块做重切分缓冲（会话级状态）。
        private readonly MemoryStream _frameBuffer = new();

        public int SampleRate => 16000;

        public string DisplayName => "讯飞";

        public Uri BuildUri(SpeechResource resource, AzureSpeechConfig config)
        {
            var appId = resource.AppId.Trim();
            var apiKey = resource.ApiKey.Trim();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            // baseString = MD5(appId + ts); signa = base64(HmacSHA1(baseString, apiKey))
            var md5 = Md5Hex(appId + ts);
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(apiKey));
            var signa = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(md5)));

            var lang = MapRtasrLanguage(config.SourceLanguage);
            var sb = new StringBuilder($"wss://{RtasrHost}/v1/ws?");
            sb.Append("appid=").Append(Uri.EscapeDataString(appId));
            sb.Append("&ts=").Append(Uri.EscapeDataString(ts));
            sb.Append("&signa=").Append(Uri.EscapeDataString(signa));
            if (!string.IsNullOrEmpty(lang))
            {
                sb.Append("&lang=").Append(Uri.EscapeDataString(lang));
            }

            return new Uri(sb.ToString());
        }

        public Task OnConnectedAsync(SpeechResource resource, AzureSpeechConfig config, IAsrUplink uplink, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public async Task OnAudioChunkAsync(byte[] pcm16, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            _frameBuffer.Write(pcm16, 0, pcm16.Length);
            await FlushFramesAsync(uplink, flushRemainder: false, cancellationToken).ConfigureAwait(false);
        }

        public async Task OnCompletedAsync(IAsrUplink uplink, CancellationToken cancellationToken)
        {
            await FlushFramesAsync(uplink, flushRemainder: true, cancellationToken).ConfigureAwait(false);
            await uplink.SendTextAsync("{\"end\": true}", cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushFramesAsync(IAsrUplink uplink, bool flushRemainder, CancellationToken cancellationToken)
        {
            var data = _frameBuffer.ToArray();
            var offset = 0;
            while (data.Length - offset >= FrameSize)
            {
                var frame = new byte[FrameSize];
                Array.Copy(data, offset, frame, 0, FrameSize);
                await uplink.SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
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
                    await uplink.SendBinaryAsync(tail, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (remaining > 0)
            {
                _frameBuffer.Write(data, offset, remaining);
            }
        }

        public void HandleMessage(string json, IAsrResultObserver observer)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() : null;
            if (string.Equals(action, "error", StringComparison.OrdinalIgnoreCase))
            {
                var desc = root.TryGetProperty("desc", out var descEl) ? descEl.GetString() : "未知错误";
                var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                observer.LogDebug($"RTASR 错误 action=error code={code} desc={desc}");
                observer.ReportError($"讯飞 RTASR 错误: {desc}");
                return;
            }

            if (!string.Equals(action, "result", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(action, "started", StringComparison.OrdinalIgnoreCase))
                {
                    observer.LogDebug("RTASR 握手成功 action=started");
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

            ParseRtasrResult(inner, observer);
        }

        private static void ParseRtasrResult(string inner, IAsrResultObserver observer)
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
                observer.ReportFinal(text);
            }
            else
            {
                observer.ReportPartial(text);
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

        public bool IsProbeResponseSuccess(string message, out string detail)
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
    }
}
