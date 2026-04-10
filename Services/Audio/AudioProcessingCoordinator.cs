using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 统一编排：采集分轨帧 -> 插件预处理 -> 同时分发到录音与识别。
    /// 目标是让录音和识别听到同一份处理后音频，避免双管线偏差。
    /// </summary>
    public sealed class AudioProcessingCoordinator : IDisposable
    {
        private readonly IAudioPreProcessor _preProcessor;
        private readonly IAudioRecognitionSink _recognitionSink;
        private readonly IAudioRecordingSink? _recordingSink;
        private readonly AutoGainProcessor? _recognitionAutoGain;
        private readonly VadGateController? _vadGate;
        private bool _recognizeLoopback;
        private bool _recognizeMic;
        private bool _recordLoopback;
        private bool _recordMic;

        /// <summary>当前使用的预处理器</summary>
        public IAudioPreProcessor PreProcessor => _preProcessor;

        /// <summary>VAD 门控器（仅双路模式下有值）</summary>
        public VadGateController? VadGate => _vadGate;

        public AudioProcessingCoordinator(
            IAudioPreProcessor preProcessor,
            IAudioRecognitionSink recognitionSink,
            IAudioRecordingSink? recordingSink,
            bool recognizeLoopback,
            bool recognizeMic,
            bool recordLoopback,
            bool recordMic,
            bool recognitionAutoGainEnabled,
            double targetRms,
            double minGain,
            double maxGain,
            double smoothing,
            VadGateController? vadGate = null)
        {
            _preProcessor = preProcessor;
            _recognitionSink = recognitionSink;
            _recordingSink = recordingSink;
            _recognizeLoopback = recognizeLoopback;
            _recognizeMic = recognizeMic;
            _recordLoopback = recordLoopback;
            _recordMic = recordMic;
            _recognitionAutoGain = recognitionAutoGainEnabled
                ? new AutoGainProcessor(targetRms, minGain, maxGain, smoothing)
                : null;
            _vadGate = vadGate;
        }

        public void UpdateRouting(bool recognizeLoopback, bool recognizeMic, bool recordLoopback, bool recordMic)
        {
            _recognizeLoopback = recognizeLoopback;
            _recognizeMic = recognizeMic;
            _recordLoopback = recordLoopback;
            _recordMic = recordMic;
        }

        public byte[] ProcessAndDispatch(CapturedAudioFrame frame)
        {
            var cleanMic = _preProcessor.Process(frame);

            // ── 识别路径：如果启用 VAD 门控且双路都在用，按门控选源 ──
            byte[] recognitionChunk;
            if (_vadGate != null && _recognizeMic && _recognizeLoopback)
            {
                var micRms = VadGateController.ComputeRmsPcm16(cleanMic);
                var loopRms = VadGateController.ComputeRmsPcm16(frame.ReferencePcm16);
                var source = _vadGate.Update(micRms, loopRms);

                recognitionChunk = source switch
                {
                    VadGateController.ActiveSource.Mic
                        => Pcm16AudioMixer.CloneOrSilence(cleanMic, frame.ByteLength),
                    VadGateController.ActiveSource.Loopback
                        => Pcm16AudioMixer.CloneOrSilence(frame.ReferencePcm16, frame.ByteLength),
                    _ => new byte[frame.ByteLength]
                };
            }
            else
            {
                // 单路或未启用门控 → 走原有混合逻辑
                recognitionChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recognizeMic, _recognizeLoopback, frame.ByteLength);
            }

            _recognitionAutoGain?.ProcessInPlace(recognitionChunk, recognitionChunk.Length);
            _recognitionSink.WriteChunk(recognitionChunk);

            // ── 录音路径：始终混合落盘，不受 VAD 门控影响 ──
            if (_recordingSink != null)
            {
                var recordingChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recordMic, _recordLoopback, frame.ByteLength);
                _recordingSink.WriteChunk(recordingChunk);
            }

            return recognitionChunk;
        }

        public void CloseRecognition()
        {
            _recognitionSink.Close();
        }

        private static byte[] BuildOutputChunk(byte[] cleanMic, byte[] reference, bool includeMic, bool includeReference, int fallbackLength)
        {
            if (includeMic && includeReference)
            {
                return Pcm16AudioMixer.MixMono(
                    Pcm16AudioMixer.CloneOrSilence(cleanMic, fallbackLength),
                    Pcm16AudioMixer.CloneOrSilence(reference, fallbackLength));
            }

            if (includeMic)
            {
                return Pcm16AudioMixer.CloneOrSilence(cleanMic, fallbackLength);
            }

            if (includeReference)
            {
                return Pcm16AudioMixer.CloneOrSilence(reference, fallbackLength);
            }

            return fallbackLength > 0 ? new byte[fallbackLength] : Array.Empty<byte>();
        }

        public void Dispose()
        {
            _recordingSink?.Dispose();
            _recognitionSink.Dispose();
            _preProcessor.Dispose();
        }
    }
}
