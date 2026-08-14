using System;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.RealtimeSpeech.Asr
{
    /// <summary>上行通道抽象：ASR 连接器通过它把帧/控制消息发往 WebSocket，不感知具体 socket。</summary>
    public interface IAsrUplink
    {
        Task SendBinaryAsync(byte[] payload, CancellationToken cancellationToken);
        Task SendTextAsync(string text, CancellationToken cancellationToken);
    }

    /// <summary>识别结果观察者：连接器解析服务端消息后，通过它回报中间/定稿/错误/状态。</summary>
    public interface IAsrResultObserver
    {
        /// <summary>中间识别结果（仅展示原文，不触发翻译）。</summary>
        void ReportPartial(string? text);

        /// <summary>定稿句（触发翻译）。</summary>
        void ReportFinal(string? text);

        /// <summary>识别错误（面向用户的中文提示）。</summary>
        void ReportError(string message);

        /// <summary>诊断/调试日志。</summary>
        void LogDebug(string message);
    }

    /// <summary>
    /// ASR 角色积木：封装某厂商「实时语音识别」的全部协议差异
    /// （连接地址、鉴权/起始帧、帧切分、收尾帧、消息解析）。
    /// 每个厂商一个实现、一个文件；连接器实例按会话创建（可持有切帧缓冲等会话状态）。
    /// </summary>
    public interface IAsrConnector
    {
        /// <summary>ASR 采样率（讯飞/百度均为 16000）。</summary>
        int SampleRate { get; }

        /// <summary>厂商显示名（用于状态提示与日志）。</summary>
        string DisplayName { get; }

        /// <summary>构建 WebSocket 连接地址。</summary>
        Uri BuildUri(SpeechResource resource, AzureSpeechConfig config);

        /// <summary>连接成功后发送起始控制帧（如百度 START）。默认不发送。</summary>
        Task OnConnectedAsync(SpeechResource resource, AzureSpeechConfig config, IAsrUplink uplink, CancellationToken cancellationToken);

        /// <summary>处理一段裸 PCM16（连接器负责按厂商帧约束重切分并通过 uplink 发送）。</summary>
        Task OnAudioChunkAsync(byte[] pcm16, IAsrUplink uplink, CancellationToken cancellationToken);

        /// <summary>音频结束时发送收尾控制帧（讯飞 {"end":true} / 百度 FINISH）。</summary>
        Task OnCompletedAsync(IAsrUplink uplink, CancellationToken cancellationToken);

        /// <summary>解析服务端文本消息，通过 observer 回报中间/定稿/错误。</summary>
        void HandleMessage(string json, IAsrResultObserver observer);

        /// <summary>探活时判断服务端首帧是否表示握手成功（默认仅凭“收到任意消息”判定成功）。</summary>
        bool IsProbeResponseSuccess(string message, out string detail);
    }
}
