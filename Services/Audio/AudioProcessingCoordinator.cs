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
        private readonly Action<bool>? _onContestStateChanged;
        private bool _lastContestActive;

        /// <summary>锁定下，「对手」最近一次活跃起的余热帧数（用于决定录音是否混合）。
        /// 只要 cooldown>0 就限意处于争抢窗口——录音走双路混合；递减到 0 才切回单路。</summary>
        private int _otherSideActiveCooldown;
        private const int OtherSideActiveCooldownFrames = 50; // ≈30ms*50 = 1500ms

        /// <summary>录音模式枚举（用于 crossfade）。</summary>
        private enum RecordingMode { Silence, MicOnly, LoopbackOnly, Mix }
        private RecordingMode _prevRecordingMode = RecordingMode.Silence;
        /// <summary>切换时交叉淡化的样本数 (5ms @ 16kHz = 80 samples)。</summary>
        private const int CrossfadeRampSamples = 80;
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
            bool useVadGatedRecording = false,
            Action<bool>? onContestStateChanged = null)
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
            _onContestStateChanged = onContestStateChanged;
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
            ActiveSpeakerSnapshot? vadSnapshot = null;
            if (_vadGate != null && _recognizeMic && _recognizeLoopback)
            {
                var micRms = VadGateController.ComputeRmsPcm16(cleanMic);
                var loopRms = VadGateController.ComputeRmsPcm16(frame.ReferencePcm16);
                var snapshot = _vadGate.UpdateDetailed(micRms, loopRms);
                vadSnapshot = snapshot;
                var source = snapshot.ActiveSource;
                vadSelectedSource = source;

                // 「对手活跃 cooldown」策略：
                //   - 锁定方为 Mic 时，对手 = loopActive；锁定方为 Loopback 时，对手 = micActive
                //   - 对手一旦活跃：立即点亮 cooldown（零延迟），录音走混合保留双方
                //   - 对手连续静默：cooldown 递减到 0，才切回单路（明确一方不说话后才闭掉一路）
                bool otherActive = source switch
                {
                    VadGateController.ActiveSource.Mic => snapshot.LoopbackActive,
                    VadGateController.ActiveSource.Loopback => snapshot.MicActive,
                    _ => false
                };
                if (source == VadGateController.ActiveSource.None)
                {
                    _otherSideActiveCooldown = 0;
                }
                else if (otherActive)
                {
                    _otherSideActiveCooldown = OtherSideActiveCooldownFrames;
                }
                else if (_otherSideActiveCooldown > 0)
                {
                    _otherSideActiveCooldown--;
                }
                var nowContestActive = _otherSideActiveCooldown > 0;
                if (nowContestActive != _lastContestActive)
                {
                    _lastContestActive = nowContestActive;
                    _onContestStateChanged?.Invoke(nowContestActive);
                }

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
                        _recognizeMic,
                        _recognizeLoopback,
                        false));
                }
            }

            //   - 安静 / 单方独占：保持单方 switch（清晰）
            //   - 争抢窗口 / DualOutput 期：临时切回 MixMono 混合双路，确保录音不丢任何一方
            //   - 模式切换那一帧会做 5ms 交叉淡化 (crossfade) 消除咕噜声
            if (_recordingSink != null)
            {
                byte[] recordingChunk;
                RecordingMode currentMode;
                if (_useVadGatedRecording && vadSelectedSource.HasValue && _recordMic && _recordLoopback)
                {
                    // 使用上方计算的 _otherSideActiveCooldown：>0 即争抢窗口，录音立即混合。
                    if (_otherSideActiveCooldown > 0)
                    {
                        currentMode = RecordingMode.Mix;
                    }
                    else
                    {
                        currentMode = vadSelectedSource.Value switch
                        {
                            VadGateController.ActiveSource.Mic => RecordingMode.MicOnly,
                            VadGateController.ActiveSource.Loopback => RecordingMode.LoopbackOnly,
                            _ => RecordingMode.Silence
                        };
                    }
                    recordingChunk = BuildModeChunk(currentMode, cleanMic, frame.ReferencePcm16, frame.ByteLength);

                    // 模式变化那一帧：与「如果还按上一帧模式生成」的波形做前 5ms 线性交叉淡化，消除跳变咬耳
                    if (_prevRecordingMode != RecordingMode.Silence && currentMode != _prevRecordingMode)
                    {
                        var prevModeChunk = BuildModeChunk(_prevRecordingMode, cleanMic, frame.ReferencePcm16, frame.ByteLength);
                        ApplyCrossfade(prevModeChunk, recordingChunk, CrossfadeRampSamples);
                    }
                    _prevRecordingMode = currentMode;
                }
                else
                {
                    recordingChunk = BuildOutputChunk(cleanMic, frame.ReferencePcm16, _recordMic, _recordLoopback, frame.ByteLength);
                    _prevRecordingMode = RecordingMode.Silence; // 重置，避免跨路径误 crossfade
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

        /// <summary>按指定录音模式生成本帧 PCM16 数据（用于 crossfade 时同时生成"旧模式""新模式"两份）。</summary>
        private static byte[] BuildModeChunk(RecordingMode mode, byte[] cleanMic, byte[] reference, int byteLength)
        {
            return mode switch
            {
                RecordingMode.MicOnly => Pcm16AudioMixer.CloneOrSilence(cleanMic, byteLength),
                RecordingMode.LoopbackOnly => Pcm16AudioMixer.CloneOrSilence(reference, byteLength),
                RecordingMode.Mix => Pcm16AudioMixer.MixMono(
                    Pcm16AudioMixer.CloneOrSilence(cleanMic, byteLength),
                    Pcm16AudioMixer.CloneOrSilence(reference, byteLength)),
                _ => new byte[byteLength]
            };
        }

        /// <summary>
        /// 在 newChunk 的前 rampSamples 个样本上做线性交叉淡化，使其从 prevChunk 的波形平滑过渡到 newChunk。
        /// 直接就地修改 newChunk。两 chunk 必须等长且为 PCM16 单声道。
        /// </summary>
        private static void ApplyCrossfade(byte[] prevChunk, byte[] newChunk, int rampSamples)
        {
            if (prevChunk.Length != newChunk.Length || prevChunk.Length < 2)
            {
                return;
            }
            int totalSamples = newChunk.Length / 2;
            int n = Math.Min(rampSamples, totalSamples);
            if (n <= 0) return;
            for (int i = 0; i < n; i++)
            {
                short oldSample = (short)(prevChunk[i * 2] | (prevChunk[i * 2 + 1] << 8));
                short newSample = (short)(newChunk[i * 2] | (newChunk[i * 2 + 1] << 8));
                double t = (i + 1) / (double)n; // 从 ~0 → 1
                int mixed = (int)(oldSample * (1.0 - t) + newSample * t);
                if (mixed > short.MaxValue) mixed = short.MaxValue;
                else if (mixed < short.MinValue) mixed = short.MinValue;
                newChunk[i * 2] = (byte)(mixed & 0xFF);
                newChunk[i * 2 + 1] = (byte)((mixed >> 8) & 0xFF);
            }
        }

        public void Dispose()
        {
            _recordingSink?.Dispose();
            _recognitionSink.Dispose();
            _preProcessor.Dispose();
        }
    }
}
