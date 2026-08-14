using System;

namespace TrueFluentPro.Services.RealtimeSpeech.Pipeline
{
    /// <summary>
    /// 厂商无关的中性音频帧：纯 PCM16 数据 + 必要元信息。
    /// 刻意不引用任何 Speech SDK 类型，使其可在录音、识别投递等任意积木之间流转。
    /// </summary>
    public readonly struct AudioFrame
    {
        public AudioFrame(byte[] pcm16, int sampleRate, int channels, DateTime captureUtc)
        {
            Pcm16 = pcm16 ?? Array.Empty<byte>();
            SampleRate = sampleRate;
            Channels = channels <= 0 ? 1 : channels;
            CaptureUtc = captureUtc;
        }

        /// <summary>小端 PCM16 数据（长度即有效字节数）。</summary>
        public byte[] Pcm16 { get; }

        public int SampleRate { get; }

        public int Channels { get; }

        public DateTime CaptureUtc { get; }

        public int Length => Pcm16.Length;

        /// <summary>复制一份独立的帧（含字节数组），用于需要改写内容又不能污染其它分支的处理积木。</summary>
        public AudioFrame Clone()
        {
            var copy = new byte[Pcm16.Length];
            Buffer.BlockCopy(Pcm16, 0, copy, 0, Pcm16.Length);
            return new AudioFrame(copy, SampleRate, Channels, CaptureUtc);
        }

        /// <summary>以新的 PCM 数据派生一帧，保留采样率/声道/时间戳元信息。</summary>
        public AudioFrame WithData(byte[] pcm16) => new(pcm16, SampleRate, Channels, CaptureUtc);
    }
}
