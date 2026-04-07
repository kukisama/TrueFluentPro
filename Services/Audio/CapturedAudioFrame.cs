using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 一帧采集到的原始音频数据：麦克风轨 + 参考轨（通常是系统回放/喇叭）。
    /// 两路均为相同采样率、16-bit PCM、单声道、相同长度的 chunk。
    /// </summary>
    public sealed class CapturedAudioFrame
    {
        public CapturedAudioFrame(byte[] micPcm16, byte[] referencePcm16, int sampleRate)
        {
            MicPcm16 = micPcm16 ?? Array.Empty<byte>();
            ReferencePcm16 = referencePcm16 ?? Array.Empty<byte>();
            SampleRate = sampleRate;
        }

        public byte[] MicPcm16 { get; }
        public byte[] ReferencePcm16 { get; }
        public int SampleRate { get; }
        public int ByteLength => Math.Max(MicPcm16.Length, ReferencePcm16.Length);
        public bool HasMic => MicPcm16.Length > 0;
        public bool HasReference => ReferencePcm16.Length > 0;
    }
}
