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
    /// 百度实时语音识别（百度智能云 BCE）连接器。协议：wss://vop.baidu.com/realtime_asr。
    /// START 帧携带 appid(数字) + appkey + dev_pid；音频按 5120 字节(160ms)切帧；收尾发 FINISH。
    /// 仅封装识别协议；翻译由独立的 ITranslationProvider 负责。
    /// </summary>
    public sealed class BaiduAsrConnector : IAsrConnector
    {
        private const int ChunkSize = 5120; // 16k 16bit 单声道 160ms = 5120 字节

        // 实时识别建议每 160ms 发一块，需对采集到的块做重切分缓冲（会话级状态）。
        private readonly MemoryStream _frameBuffer = new();

        public int SampleRate => 16000;

        public string DisplayName => "百度";

        public Uri BuildUri(SpeechResource resource, AzureSpeechConfig config)
        {
            var sn = BuildSerialNumber();
            return new Uri($"wss://vop.baidu.com/realtime_asr?sn={sn}");
        }

        public async Task OnConnectedAsync(SpeechResource resource, AzureSpeechConfig config, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            var appId = ParseAppId(resource.AppId);
            var devPid = MapDevPid(config.SourceLanguage);

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

            await uplink.SendTextAsync(JsonSerializer.Serialize(startPayload), cancellationToken).ConfigureAwait(false);
        }

        public async Task OnAudioChunkAsync(byte[] pcm16, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            _frameBuffer.Write(pcm16, 0, pcm16.Length);
            await FlushFramesAsync(uplink, flushRemainder: false, cancellationToken).ConfigureAwait(false);
        }

        public async Task OnCompletedAsync(IAsrUplink uplink, CancellationToken cancellationToken)
        {
            await FlushFramesAsync(uplink, flushRemainder: true, cancellationToken).ConfigureAwait(false);
            await uplink.SendTextAsync("{\"type\":\"FINISH\"}", cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushFramesAsync(IAsrUplink uplink, bool flushRemainder, CancellationToken cancellationToken)
        {
            var data = _frameBuffer.ToArray();
            var offset = 0;
            while (data.Length - offset >= ChunkSize)
            {
                var frame = new byte[ChunkSize];
                Array.Copy(data, offset, frame, 0, ChunkSize);
                await uplink.SendBinaryAsync(frame, cancellationToken).ConfigureAwait(false);
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

            if (root.TryGetProperty("err_no", out var errNoEl)
                && errNoEl.ValueKind == JsonValueKind.Number
                && errNoEl.GetInt32() != 0)
            {
                var errMsg = root.TryGetProperty("err_msg", out var errMsgEl) ? errMsgEl.GetString() : "未知错误";
                var errNo = errNoEl.GetInt32();
                observer.LogDebug($"识别错误 err_no={errNo} err_msg={errMsg}");
                observer.ReportError($"百度实时语音错误(err_no={errNo}): {errMsg}{DescribeAsrError(errNo)}");
                return;
            }

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var result = root.TryGetProperty("result", out var resultEl) ? resultEl.GetString() : null;

            switch (type)
            {
                case "MID_TEXT":
                    observer.ReportPartial(result);
                    break;
                case "FIN_TEXT":
                    observer.ReportFinal(result);
                    break;
                case "HEARTBEAT":
                    break;
                default:
                    observer.LogDebug($"其他消息 type={type ?? "(无)"}");
                    break;
            }
        }

        public bool IsProbeResponseSuccess(string message, out string detail)
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
                // 非 JSON 首帧不视为失败。
            }

            return true;
        }

        /// <summary>
        /// 按源语言映射百度实时识别 dev_pid（开放平台模型，加强标点）：15372=中文普通话，17372=英语。
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
                _ when lower.StartsWith("en") => 17372,
                _ when lower.StartsWith("zh") => 15372,
                _ => 15372
            };
        }

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
    }
}
