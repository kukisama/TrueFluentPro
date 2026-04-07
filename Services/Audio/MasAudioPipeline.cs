using System;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 封装 Microsoft Audio Stack (MAS) 管线的创建逻辑。
    /// MAS 在 Speech SDK 内部对 PushStream 数据做 AEC/NS 等处理后再送识别，
    /// 本类只负责创建正确的 PushStream + AudioConfig 组合，不涉及 NAudio 采集或识别器管理。
    /// 支持两种模式：
    /// 1. 双通道（mic + reference）→ 适合 MAS AEC。
    /// 2. 单通道识别增强 → 适合在应用层主插件处理后，再给云端做额外 NS/去混响增强。
    /// </summary>
    public sealed class MasAudioPipeline : IDisposable
    {
        /// <summary>往这里 Write() 双通道交错 PCM（ch1=mic, ch2=loopback）。</summary>
        public PushAudioInputStream PushStream { get; }

        /// <summary>传给 TranslationRecognizer 的 AudioConfig，内含 MAS 处理选项。</summary>
        public AudioConfig AudioConfig { get; }

        /// <summary>实际生效的处理标志位（for diagnostic）。</summary>
        public int ProcessingFlags { get; }

        private MasAudioPipeline(PushAudioInputStream pushStream, AudioConfig audioConfig, int flags)
        {
            PushStream = pushStream;
            AudioConfig = audioConfig;
            ProcessingFlags = flags;
        }

        /// <summary>
        /// 根据配置创建 MAS 管线。
        /// </summary>
        /// <param name="sampleRate">采样率，默认 16000</param>
        /// <param name="echoCancellation">是否启用回声消除</param>
        /// <param name="noiseSuppression">是否启用降噪</param>
        /// <returns>封装好的管线，调用方负责 Dispose。</returns>
        public static MasAudioPipeline Create(
            int sampleRate = 16000,
            bool echoCancellation = true,
            bool noiseSuppression = true)
        {
            // 构造处理标志位
            var flags = AudioProcessingConstants.AUDIO_INPUT_PROCESSING_ENABLE_DEFAULT;
            if (!echoCancellation)
                flags |= AudioProcessingConstants.AUDIO_INPUT_PROCESSING_DISABLE_ECHO_CANCELLATION;
            if (!noiseSuppression)
                flags |= AudioProcessingConstants.AUDIO_INPUT_PROCESSING_DISABLE_NOISE_SUPPRESSION;

            // 单麦克风几何 + loopback 作为最后一个通道
            var processingOptions = AudioProcessingOptions.Create(
                flags,
                PresetMicrophoneArrayGeometry.Mono,
                SpeakerReferenceChannel.LastChannel);

            // 2 声道 PushStream：ch1=mic, ch2=loopback(reference)
            var streamFormat = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 2);
            var pushStream = AudioInputStream.CreatePushStream(streamFormat);
            var audioConfig = AudioConfig.FromStreamInput(pushStream, processingOptions);

            return new MasAudioPipeline(pushStream, audioConfig, flags);
        }

        /// <summary>
        /// 创建单通道识别增强 MAS 管线。
        /// 这里不再使用 reference 通道，因此回声消除不会生效，主要用于云端前的额外降噪/去混响增强。
        /// </summary>
        public static MasAudioPipeline CreateRecognitionEnhancement(
            int sampleRate = 16000,
            bool noiseSuppression = true,
            Action<string>? log = null)
        {
            var flags = AudioProcessingConstants.AUDIO_INPUT_PROCESSING_ENABLE_DEFAULT |
                        AudioProcessingConstants.AUDIO_INPUT_PROCESSING_DISABLE_ECHO_CANCELLATION;

            if (!noiseSuppression)
            {
                flags |= AudioProcessingConstants.AUDIO_INPUT_PROCESSING_DISABLE_NOISE_SUPPRESSION;
            }

            var processingOptions = AudioProcessingOptions.Create(flags);
            var streamFormat = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 1);
            var pushStream = AudioInputStream.CreatePushStream(streamFormat);
            var audioConfig = AudioConfig.FromStreamInput(pushStream, processingOptions);
            log?.Invoke($"[MAS] 已启用识别增强 单通道模式 NS={(noiseSuppression ? "开" : "关")} AEC=关");
            return new MasAudioPipeline(pushStream, audioConfig, flags);
        }

        /// <summary>
        /// 创建不带 MAS 处理的普通单通道管线（关闭态回退）。
        /// </summary>
        public static (PushAudioInputStream pushStream, AudioConfig audioConfig) CreateBypass(int sampleRate = 16000)
        {
            var streamFormat = AudioStreamFormat.GetWaveFormatPCM((uint)sampleRate, 16, 1);
            var pushStream = AudioInputStream.CreatePushStream(streamFormat);
            var audioConfig = AudioConfig.FromStreamInput(pushStream);
            return (pushStream, audioConfig);
        }

        public void Dispose()
        {
            AudioConfig.Dispose();
            PushStream.Dispose();
        }
    }
}
