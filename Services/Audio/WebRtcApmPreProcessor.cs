using System;
using TrueFluentPro.Models;
using SoundFlow.Extensions.WebRtc.Apm;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 基于 SoundFlow.Extensions.WebRtc.Apm 低层 API 的真实 WebRTC APM 预处理器。
    /// 直接使用 AudioProcessingModule / ApmConfig / StreamConfig 处理 PCM chunk，
    /// 不依赖 SoundFlow 的完整实时音频图。
    /// </summary>
    public sealed class WebRtcApmPreProcessor : IAudioPreProcessor
    {
        private readonly AudioProcessingModule? _module;
        private readonly StreamConfig? _streamConfig;
        private readonly int _sampleRate;
        private readonly int _frameSamples;
        private readonly int _frameBytes;
        private readonly bool _aecEnabled;
        private readonly Action<string>? _log;
        private bool _runtimeFailureLogged;

        public string Id => "webrtc-apm";
        public string DisplayName => "WebRTC APM";
        public bool IsAvailable { get; }
        public string? UnavailableReason { get; }

        public WebRtcApmPreProcessor(AzureSpeechConfig config, Action<string>? log = null)
        {
            _log = log;
            try
            {
                _sampleRate = 16000;
                _frameSamples = _sampleRate / 100; // 10ms
                _frameBytes = _frameSamples * 2;
                _aecEnabled = config.WebRtcAecEnabled;

                _module = new AudioProcessingModule();
                using var apmConfig = new ApmConfig();
                apmConfig.SetEchoCanceller(config.WebRtcAecEnabled, config.WebRtcAecMobileMode);
                apmConfig.SetNoiseSuppression(config.WebRtcNoiseSuppressionEnabled, (NoiseSuppressionLevel)Math.Clamp(config.WebRtcNoiseSuppressionLevel, 0, 3));
                apmConfig.SetGainController1(
                    config.WebRtcAgc1Enabled,
                    (GainControlMode)Math.Clamp(config.WebRtcAgcMode, 0, 2),
                    Math.Clamp(config.WebRtcAgcTargetLevelDbfs, -31, 0),
                    Math.Clamp(config.WebRtcAgcCompressionGainDb, 0, 90),
                    config.WebRtcAgcLimiterEnabled);
                apmConfig.SetGainController2(config.WebRtcAgc2Enabled);
                apmConfig.SetHighPassFilter(config.WebRtcHighPassFilterEnabled);
                apmConfig.SetPreAmplifier(config.WebRtcPreAmpEnabled, Math.Clamp(config.WebRtcPreAmpGain, 0.5f, 8.0f));
                apmConfig.SetPipeline(_sampleRate, false, false, DownmixMethod.AverageChannels);

                var applyErr = _module.ApplyConfig(apmConfig);
                var initErr = _module.Initialize();
                _module.SetStreamDelayMs(Math.Clamp(config.WebRtcAecLatencyMs, 0, 500));
                _streamConfig = new StreamConfig(_sampleRate, 1);

                if (applyErr != ApmError.NoError || initErr != ApmError.NoError)
                {
                    IsAvailable = false;
                    UnavailableReason = $"WebRTC APM 初始化失败 Apply={applyErr}, Init={initErr}";
                    log?.Invoke($"[音频插件] {UnavailableReason}");
                    return;
                }

                IsAvailable = true;
                UnavailableReason = null;
                log?.Invoke($"[音频插件] WebRTC APM 已启用 AEC={config.WebRtcAecEnabled} NS={config.WebRtcNoiseSuppressionEnabled} NS级别={(NoiseSuppressionLevel)Math.Clamp(config.WebRtcNoiseSuppressionLevel, 0, 3)} AGC1={config.WebRtcAgc1Enabled} HPF={config.WebRtcHighPassFilterEnabled}");
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                UnavailableReason = ex.Message;
                log?.Invoke($"[音频插件] WebRTC APM 加载失败: {ex.Message}");
            }
        }

        public byte[] Process(CapturedAudioFrame frame)
        {
            if (!IsAvailable || _module == null || _streamConfig == null)
            {
                return CloneOrSilence(frame.MicPcm16, frame.ByteLength);
            }

            try
            {
                var input = CloneOrSilence(frame.MicPcm16, frame.ByteLength);
                var reference = CloneOrSilence(frame.ReferencePcm16, input.Length);
                var output = new byte[input.Length];

                var offset = 0;
                while (offset < input.Length)
                {
                    var chunkLen = Math.Min(_frameBytes, input.Length - offset);
                    if (chunkLen <= 0)
                    {
                        break;
                    }

                    var micFrame = ExtractFloatFrame(input, offset, chunkLen, _frameSamples);
                    var outFrame = new[] { new float[_frameSamples] };

                    if (_aecEnabled)
                    {
                        var refFrame = ExtractFloatFrame(reference, offset, chunkLen, _frameSamples);
                        var reverseOut = new[] { new float[_frameSamples] };
                        _module.ProcessReverseStream(refFrame, _streamConfig, _streamConfig, reverseOut);
                    }

                    var err = _module.ProcessStream(micFrame, _streamConfig, _streamConfig, outFrame);
                    if (err != ApmError.NoError)
                    {
                        WritePcm16(output, offset, micFrame[0], chunkLen / 2);
                    }
                    else
                    {
                        WritePcm16(output, offset, outFrame[0], chunkLen / 2);
                    }

                    offset += chunkLen;
                }

                return output;
            }
            catch (Exception ex)
            {
                if (!_runtimeFailureLogged)
                {
                    _runtimeFailureLogged = true;
                    _log?.Invoke($"[音频插件] WebRTC APM 运行时处理失败（已回退透传）: {ex.Message}");
                }
                return CloneOrSilence(frame.MicPcm16, frame.ByteLength);
            }
        }

        public void Dispose()
        {
            _streamConfig?.Dispose();
            _module?.Dispose();
        }

        private static byte[] CloneOrSilence(byte[]? source, int fallbackLength)
        {
            if (source == null || source.Length == 0)
            {
                return fallbackLength > 0 ? new byte[fallbackLength] : Array.Empty<byte>();
            }

            var clone = new byte[source.Length];
            Buffer.BlockCopy(source, 0, clone, 0, source.Length);
            return clone;
        }

        private static float[][] ExtractFloatFrame(byte[] pcm16, int offset, int count, int frameSamples)
        {
            var samples = new float[frameSamples];
            var sampleCount = Math.Min(frameSamples, count / 2);
            for (var i = 0; i < sampleCount; i++)
            {
                var index = offset + (i * 2);
                var value = (short)(pcm16[index] | (pcm16[index + 1] << 8));
                samples[i] = value / 32768f;
            }

            return new[] { samples };
        }

        private static void WritePcm16(byte[] target, int offset, float[] samples, int sampleCount)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var clamped = Math.Clamp(samples[i], -1f, 1f);
                var sample = (short)Math.Round(clamped * short.MaxValue);
                var index = offset + (i * 2);
                if (index + 1 >= target.Length)
                {
                    break;
                }

                target[index] = (byte)(sample & 0xFF);
                target[index + 1] = (byte)((sample >> 8) & 0xFF);
            }
        }
    }
}
