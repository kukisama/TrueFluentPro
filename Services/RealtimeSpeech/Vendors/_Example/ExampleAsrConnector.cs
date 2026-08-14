using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.RealtimeSpeech.Asr;

namespace TrueFluentPro.Services.RealtimeSpeech.Vendors.Example
{
    /// <summary>
    /// ════════════════════════════════════════════════════════════════════════
    /// 【示例·识别积木】演示如何为一个新厂商接入「实时语音识别（ASR）」。
    /// ════════════════════════════════════════════════════════════════════════
    ///
    /// 这是一个**可编译、带满注释的样板**。想接入新厂商时，复制本文件改个名即可。
    /// 你只需实现 IAsrConnector 的几个方法，**完全不用改核心编排代码**
    /// （CascadedRealtimeSpeechService 会自动调用你这里的方法）。
    ///
    /// ── 一句话理解 ──
    /// 核心负责「开麦克风、连 WebSocket、收发、落字幕」这些通用流程；
    /// 你这块积木只负责「这个厂商的协议细节」：连哪个地址、怎么鉴权、
    /// 音频怎么切帧发、服务端消息怎么解析成文字。
    ///
    /// ── 生命周期（核心会按顺序调用）──
    ///   1. BuildUri            ：告诉核心要连哪个 wss 地址
    ///   2. OnConnectedAsync    ：连上后发起始帧 / 鉴权帧（没有就留空）
    ///   3. OnAudioChunkAsync   ：每来一小段麦克风音频(PCM16)就调一次，你负责切帧上传
    ///   4. OnCompletedAsync    ：录音停止时发收尾帧
    ///   5. HandleMessage       ：每收到一条服务端文本消息就调一次，你解析出文字回报
    ///   6. IsProbeResponseSuccess：「测试连接」时判断首帧是否代表握手成功
    /// </summary>
    public sealed class ExampleAsrConnector : IAsrConnector
    {
        // ── 采样率：绝大多数实时识别厂商都是 16000Hz 单声道 16bit。按厂商文档填。──
        public int SampleRate => 16000;

        // ── 厂商显示名：出现在状态栏与日志里。──
        public string DisplayName => "示例厂商";

        /// <summary>
        /// 【第 1 步】构建 WebSocket 连接地址。
        /// 真实厂商通常要在这里把 AppId / 时间戳 / 签名(signature) 拼进 URL 的查询参数。
        /// 凭据从 resource 里取：resource.AppId / resource.ApiKey（识别用的那一对）。
        /// </summary>
        public Uri BuildUri(SpeechResource resource, AzureSpeechConfig config)
        {
            // 示例：把 AppId 作为查询参数。真实厂商一般还要 ts + signa 签名。
            var appId = string.IsNullOrWhiteSpace(resource.AppId) ? "demo" : resource.AppId.Trim();
            return new Uri($"wss://example-vendor.invalid/realtime_asr?appid={appId}");
        }

        /// <summary>
        /// 【第 2 步】连接成功后发送起始/鉴权控制帧。
        /// 例如百度要发一个 START（含 appid/appkey/采样率）。本示例发一个最简起始帧。
        /// 若你的厂商连上就能直接发音频、不需要起始帧，本方法留空即可。
        /// </summary>
        public async Task OnConnectedAsync(SpeechResource resource, AzureSpeechConfig config, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            var startPayload = new
            {
                type = "START",
                appkey = resource.ApiKey?.Trim() ?? "",
                sample = SampleRate,
                format = "pcm"
            };
            // 通过 uplink 发送文本帧——你不用关心底层 WebSocket，核心已帮你管好。
            await uplink.SendTextAsync(JsonSerializer.Serialize(startPayload), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 【第 3 步】处理一小段裸 PCM16 音频。核心每采集到一块就调一次。
        /// 大多数厂商对单帧大小有要求（如百度每 160ms = 5120 字节一帧），
        /// 这时你需要在这里做「攒够一帧再发」的缓冲（可参考 BaiduAsrConnector 的 _frameBuffer）。
        /// 本示例为求最简，直接原样转发。
        /// </summary>
        public async Task OnAudioChunkAsync(byte[] pcm16, IAsrUplink uplink, CancellationToken cancellationToken)
        {
            await uplink.SendBinaryAsync(pcm16, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 【第 4 步】录音停止时发收尾帧，告诉服务端「说完了，把最后的结果给我」。
        /// 例如讯飞发 {"end":true}，百度发 {"type":"FINISH"}。没有就留空。
        /// </summary>
        public async Task OnCompletedAsync(IAsrUplink uplink, CancellationToken cancellationToken)
        {
            await uplink.SendTextAsync("{\"type\":\"FINISH\"}", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 【第 5 步】解析服务端发来的每条文本消息，把识别到的文字回报给核心。
        ///   · 中间结果（边说边出、会变）→ observer.ReportPartial(文字)
        ///   · 定稿结果（一句说完、不再变）→ observer.ReportFinal(文字) ← 这一步才会触发翻译
        ///   · 出错 → observer.ReportError(中文提示)
        /// 本示例约定服务端返回形如：{"type":"partial","text":"你好"} / {"type":"final","text":"你好世界"}。
        /// 真实厂商的字段名各不相同，照其文档解析即可。
        /// </summary>
        public void HandleMessage(string json, IAsrResultObserver observer)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                var text = root.TryGetProperty("text", out var textEl) ? textEl.GetString() : null;

                switch (type)
                {
                    case "partial":
                        observer.ReportPartial(text);
                        break;
                    case "final":
                        observer.ReportFinal(text);
                        break;
                    case "error":
                        observer.ReportError($"示例厂商识别错误：{text}");
                        break;
                    default:
                        observer.LogDebug($"未识别的消息类型：{type}");
                        break;
                }
            }
            catch (Exception ex)
            {
                observer.LogDebug($"解析服务端消息失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 【第 6 步】「测试连接」时，判断收到的首帧是否代表握手成功。
        /// 最简单的做法：只要服务端回了任何消息就算连通成功。
        /// 想更严谨可解析 json 看是否含成功标志（如 {"type":"started"}）。
        /// </summary>
        public bool IsProbeResponseSuccess(string message, out string detail)
        {
            detail = "已收到服务端响应，连接正常。";
            return true;
        }
    }
}
