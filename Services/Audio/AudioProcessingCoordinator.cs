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
        private readonly ActiveSpeakerTimelineStore? _timelineStore;
        private readonly bool _useVadGatedRecording;
        private bool _recognizeLoopback;
        private bool _recognizeMic;
        private bool _recordLoopback;
        private bool _recordMic;

        /// <summary>当前使用的预处理器</summary>
        public IAudioPreProcessor PreProcessor => _preProcessor;

        /// <summary>VAD 门控器（仅双路模式下有值）</summary>
        public VadGateController? VadGate => _vadGate;

        /// <summary>发言人时间轴记录（可选）。</summary>
        public ActiveSpeakerTimelineStore? TimelineStore => _timelineStore;

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
            VadGateController? vadGate = null,
            ActiveSpeakerTimelineStore? timelineStore = null,
            bool useVadGatedRecording = true)
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
            _timelineStore = timelineStore;
            _useVadGatedRecording = useVadGatedRecording;
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
            VadGateController.ActiveSource? vadSelectedSource = null;
            if (_vadGate != null && _recognizeMic && _recognizeLoopback)
            {
                var micRms = VadGateController.ComputeRmsPcm16(cleanMic);
                var loopRms = VadGateController.ComputeRmsPcm16(frame.ReferencePcm16);
                var snapshot = _vadGate.UpdateDetailed(micRms, loopRms);
                var source = snapshot.ActiveSource;
                vadSelectedSource = source;

                _timelineStore?.Append(new ActiveSpeakerSample(
                    DateTime.UtcNow,
                    source,
                    snapshot.MicRms,
                    snapshot.LoopbackRms,
                    snapshot.MicEmaRms,
                    snapshot.LoopbackEmaRms,
                    snapshot.MicActive,
                    snapshot.LoopbackActive,
                    snapshot.IsLocked));

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

                // 即使是单路，也写入时间轴快照，让 UI 能正确显示：
                // - 单麦：蓝条正常波动，红条恒为静音
                // - 单环回：红条正常波动，蓝条恒为静音
                // 未启用的那一路 RMS 直接给 0，避免显示无意义数据。
                if (_timelineStore != null)
                {
                    var micRmsRaw = _recognizeMic ? VadGateController.ComputeRmsPcm16(cleanMic) : 0.0;
                    var loopRmsRaw = _recognizeLoopback ? VadGateController.ComputeRmsPcm16(frame.ReferencePcm16) : 0.0;
                    var source = _recognizeMic && !_recognizeLoopback
                        ? VadGateController.ActiveSource.Mic
                        : (!_recognizeMic && _recognizeLoopback
                            ? VadGateController.ActiveSource.Loopback
                            : VadGateController.ActiveSource.None);
                    _timelineStore.Append(new ActiveSpeakerSample(
                        DateTime.UtcNow,
                        source,
                        micRmsRaw,
                        loopRmsRaw,
                        micRmsRaw,
                        loopRmsRaw,
                        micRmsRaw > 0.001,
                        loopRmsRaw > 0.001,
                        IsLocked: source != VadGateController.ActiveSource.None));
                }
            }

            // ── 录音路径：VAD 双路时也跟随门控选源（同一时刻只录一路），让录音更清晰 ──
            //   - 仅当 VAD 在用 且 麦+环回都勾选了录制时启用门控录音
            //   - 否则保持原混合行为
            //   - 录音不经过 AutoGain，保留原始音质
            if (_recordingSink != null)
            {
                byte[] recordingChunk;
                if (_useVadGatedRecording && vadSelectedSource.HasValue && _recordMic && _recordLoopback)
                {
                    recordingChunk = vadSelectedSource.Value switch
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
                    recordingChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recordMic, _recordLoopback, frame.ByteLength);
                }
                _recordingSink.WriteChunk(recordingChunk);
            }

            _recognitionAutoGain?.ProcessInPlace(recognitionChunk, recognitionChunk.Length);
            _recognitionSink.WriteChunk(recognitionChunk);

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
