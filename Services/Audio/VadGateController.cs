using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 仲裁模式：双方都过门限时如何选 active speaker。
    /// </summary>
    public enum VadArbitrationMode
    {
        /// <summary>静态优先级（旧行为）：永远按 conflictPriority 选。</summary>
        StaticPriority = 0,
        /// <summary>响度大者优先（平滑后比较）。</summary>
        Loudest = 1,
        /// <summary>响度差超过阈值才认为某方更响，否则回退静态优先级。</summary>
        LoudestWithMargin = 2,
    }

    /// <summary>
    /// 每次 Update 返回的运行时快照（供 UI 时间轴 / 日志使用）。
    /// </summary>
    /// <param name="IsContested">是否进入"争抢窗口"：对方能量明显高于锁定方但未到双输出阈值。</param>
    /// <param name="IsDualOutput">是否进入"双输出"硬上限态：争抢持续过久未分胜负，录音应混合保留双方。</param>
    public readonly record struct ActiveSpeakerSnapshot(
        VadGateController.ActiveSource ActiveSource,
        double MicRms,
        double LoopbackRms,
        double MicEmaRms,
        double LoopbackEmaRms,
        double MicDb,
        double LoopbackDb,
        bool MicActive,
        bool LoopbackActive,
        bool JustSwitched,
        bool IsLocked,
        bool IsContested = false,
        bool IsDualOutput = false);

    /// <summary>
    /// 句级 VAD 门控：双路音频（麦克风 + 环回）场景下，
    /// 根据 RMS 能量判断谁在说话，句级锁定后只送一路给识别器。
    /// 升级（2026-05）：EMA 平滑 / 响度仲裁 / 切换冷却 / 最小说话防抖。
    /// </summary>
    public sealed class VadGateController
    {
        public enum ActiveSource { None, Mic, Loopback }

        private readonly double _voiceThreshold;
        private readonly int _interruptionThreshold;
        private readonly int _safetyValveChunks;
        private readonly ActiveSource _conflictPriority;
        private readonly VadArbitrationMode _arbitrationMode;
        private readonly double _tieMarginDb;
        private readonly int _minSpeechChunks;
        private readonly int _switchCooldownChunks;
        private readonly double _emaAlpha;
        // 争抢窗口参数
        private readonly double _contestTriggerRatio;
        private readonly int _contestTriggerFrames;
        private readonly int _dualOutputTimeoutFrames;
        private readonly int _dualOutputExitSilenceFrames;
        private readonly Action<string>? _log;

        private ActiveSource _lockedSource = ActiveSource.None;
        private int _interruptionCount;
        private int _silentChunks;

        // 平滑后的 RMS（EMA）
        private double _micEma;
        private double _loopEma;

        // 候选源连续活跃帧计数器（最小说话防抖）
        private ActiveSource _candidateSource = ActiveSource.None;
        private int _candidateChunks;

        // 上次切换 / 锁定 之后过了多少帧（冷却控制）
        private int _framesSinceLastSwitch = int.MaxValue / 2;

        // 争抢状态机
        private int _challengerActiveFrames;       // 挑战方持续强于锁定方的帧数
        private int _contestedFramesAccumulated;   // 已进入 contested 后累计的帧数（决定何时升 dualOutput）
        private int _silenceFramesInDual;          // dualOutput 期间某方静音的累计帧数
        private bool _isContested;
        private bool _isDualOutput;

        public ActiveSource Current => _lockedSource;

        public bool IsLocked => _lockedSource != ActiveSource.None;

        public double MicEmaRms => _micEma;
        public double LoopbackEmaRms => _loopEma;

        /// <summary>
        /// 创建 VAD 门控器。
        /// </summary>
        /// <param name="voiceThreshold">RMS 低于此值视为静音，范围 0~1（默认 0.01 ≈ -40dBFS）。</param>
        /// <param name="interruptionThreshold">锁定方沉默 + 对方有声连续多少 chunk 后判定打断。</param>
        /// <param name="safetyValveChunks">两路都静音超过多少 chunk 后强制解锁。</param>
        /// <param name="conflictPriority">两人同时开口时优先谁。</param>
        /// <param name="log">可选诊断日志回调。</param>
        public VadGateController(
            double voiceThreshold = 0.01,
            int interruptionThreshold = 3,
            int safetyValveChunks = 15,
            ActiveSource conflictPriority = ActiveSource.Loopback,
            Action<string>? log = null,
            VadArbitrationMode arbitrationMode = VadArbitrationMode.Loudest,
            double tieMarginDb = 3.0,
            int minSpeechChunks = 3,
            int switchCooldownChunks = 10,
            double rmsEmaAlpha = 0.3,
            double contestTriggerRatio = 1.5,
            int contestTriggerFrames = 4,
            int dualOutputTimeoutFrames = 80,
            int dualOutputExitSilenceFrames = 50)
        {
            _voiceThreshold = Math.Clamp(voiceThreshold, 0.001, 0.5);
            _interruptionThreshold = Math.Clamp(interruptionThreshold, 1, 50);
            _safetyValveChunks = Math.Clamp(safetyValveChunks, 5, 100);
            _conflictPriority = conflictPriority == ActiveSource.None ? ActiveSource.Loopback : conflictPriority;
            _arbitrationMode = arbitrationMode;
            _tieMarginDb = Math.Clamp(tieMarginDb, 0.0, 24.0);
            _minSpeechChunks = Math.Clamp(minSpeechChunks, 1, 30);
            _switchCooldownChunks = Math.Clamp(switchCooldownChunks, 0, 100);
            _emaAlpha = Math.Clamp(rmsEmaAlpha, 0.0, 1.0);
            _contestTriggerRatio = Math.Clamp(contestTriggerRatio, 1.0, 10.0);
            _contestTriggerFrames = Math.Clamp(contestTriggerFrames, 1, 50);
            _dualOutputTimeoutFrames = Math.Clamp(dualOutputTimeoutFrames, 10, 500);
            _dualOutputExitSilenceFrames = Math.Clamp(dualOutputExitSilenceFrames, 5, 200);
            _log = log;
        }

        /// <summary>
        /// 旧 API：每个 audio chunk 调用一次，返回本次应送给识别器的来源。
        /// 内部委托给 <see cref="UpdateDetailed"/>。
        /// </summary>
        public ActiveSource Update(double micRms, double loopbackRms)
            => UpdateDetailed(micRms, loopbackRms).ActiveSource;

        /// <summary>
        /// 新 API：返回完整的运行时快照，便于 UI 时间轴 / 诊断日志使用。
        /// </summary>
        public ActiveSpeakerSnapshot UpdateDetailed(double micRms, double loopbackRms)
        {
            // 1. EMA 平滑
            if (_emaAlpha <= 0)
            {
                _micEma = micRms;
                _loopEma = loopbackRms;
            }
            else
            {
                _micEma = _emaAlpha * micRms + (1 - _emaAlpha) * _micEma;
                _loopEma = _emaAlpha * loopbackRms + (1 - _emaAlpha) * _loopEma;
            }

            var micActive = _micEma >= _voiceThreshold;
            var loopActive = _loopEma >= _voiceThreshold;

            var previousLocked = _lockedSource;
            ActiveSource next = IsLocked
                ? UpdateLocked(micActive, loopActive)
                : UpdateUnlocked(micActive, loopActive);

            var justSwitched = next != ActiveSource.None && next != previousLocked;
            if (justSwitched)
            {
                _framesSinceLastSwitch = 0;
                // 切换或重新锁定时清争抢状态
                ResetContest();
            }
            else if (_framesSinceLastSwitch < int.MaxValue / 2)
            {
                _framesSinceLastSwitch++;
            }

            EvaluateContest(micActive, loopActive);

            return new ActiveSpeakerSnapshot(
                ActiveSource: next,
                MicRms: micRms,
                LoopbackRms: loopbackRms,
                MicEmaRms: _micEma,
                LoopbackEmaRms: _loopEma,
                MicDb: RmsToDb(_micEma),
                LoopbackDb: RmsToDb(_loopEma),
                MicActive: micActive,
                LoopbackActive: loopActive,
                JustSwitched: justSwitched,
                IsLocked: next != ActiveSource.None,
                IsContested: _isContested,
                IsDualOutput: _isDualOutput);
        }

        /// <summary>
        /// 识别器 Recognized 回调时调用，释放句级锁。
        /// </summary>
        public void NotifySentenceFinalized()
        {
            if (!IsLocked)
            {
                return;
            }

            _log?.Invoke($"[VAD门控] 句子结束，解锁 (来源={_lockedSource})");
            Unlock();
        }

        /// <summary>
        /// 强制解锁（如停止翻译时调用）。
        /// </summary>
        public void Reset()
        {
            _lockedSource = ActiveSource.None;
            _interruptionCount = 0;
            _silentChunks = 0;
            _candidateSource = ActiveSource.None;
            _candidateChunks = 0;
            _framesSinceLastSwitch = int.MaxValue / 2;
            _micEma = 0;
            _loopEma = 0;
            ResetContest();
        }

        private void ResetContest()
        {
            _challengerActiveFrames = 0;
            _contestedFramesAccumulated = 0;
            _silenceFramesInDual = 0;
            _isContested = false;
            _isDualOutput = false;
        }

        /// <summary>
        /// 锁定态下评估"挑战方能量是否显著强过当前锁定方"，从而进入 / 退出 contested / dualOutput 态。
        /// 录音管线据此决定是否混合双路；识别管线仍走单路赢家，避免改 ASR 会话。
        /// </summary>
        private void EvaluateContest(bool micActive, bool loopActive)
        {
            if (!IsLocked)
            {
                ResetContest();
                return;
            }

            var lockedRms = _lockedSource == ActiveSource.Mic ? _micEma : _loopEma;
            var otherRms = _lockedSource == ActiveSource.Mic ? _loopEma : _micEma;

            // dualOutput 状态：任一方持续静音足够久就退出
            if (_isDualOutput)
            {
                if (!micActive || !loopActive)
                {
                    _silenceFramesInDual++;
                    if (_silenceFramesInDual >= _dualOutputExitSilenceFrames)
                    {
                        _log?.Invoke($"[VAD门控] DualOutput 退出，回到单方 {_lockedSource}");
                        _isDualOutput = false;
                        _isContested = false;
                        _challengerActiveFrames = 0;
                        _contestedFramesAccumulated = 0;
                        _silenceFramesInDual = 0;
                    }
                }
                else
                {
                    _silenceFramesInDual = 0;
                }
                return;
            }

            // contested 状态：累计帧数，到阈值升级为 dualOutput；锁定方明显反超则退出
            if (_isContested)
            {
                _contestedFramesAccumulated++;

                var lockedDominates = lockedRms > otherRms * _contestTriggerRatio || otherRms < _voiceThreshold;
                if (lockedDominates)
                {
                    _log?.Invoke($"[VAD门控] 争抢结束，{_lockedSource} 胜出 lockedRms={lockedRms:F4} otherRms={otherRms:F4}");
                    _isContested = false;
                    _challengerActiveFrames = 0;
                    _contestedFramesAccumulated = 0;
                    return;
                }

                if (_contestedFramesAccumulated >= _dualOutputTimeoutFrames)
                {
                    _isDualOutput = true;
                    _silenceFramesInDual = 0;
                    _log?.Invoke($"[VAD门控] 争抢持续 {_contestedFramesAccumulated} 帧未分胜负，升级 DualOutput（录音将混合双路）");
                }
                return;
            }

            // 未 contested：检测对方是否持续强于锁定方
            var challengerStronger = otherRms > lockedRms * _contestTriggerRatio && otherRms >= _voiceThreshold;
            if (challengerStronger)
            {
                _challengerActiveFrames++;
                if (_challengerActiveFrames >= _contestTriggerFrames)
                {
                    _isContested = true;
                    _contestedFramesAccumulated = 0;
                    var challenger = _lockedSource == ActiveSource.Mic ? ActiveSource.Loopback : ActiveSource.Mic;
                    _log?.Invoke($"[VAD门控] 进入争抢窗口 锁定={_lockedSource} 挑战={challenger} lockedRms={lockedRms:F4} otherRms={otherRms:F4}");
                }
            }
            else
            {
                _challengerActiveFrames = 0;
            }
            _silentChunks = 0;
            _candidateSource = ActiveSource.None;
            _candidateChunks = 0;
            _framesSinceLastSwitch = int.MaxValue / 2;
            _micEma = 0;
            _loopEma = 0;
        }

        private ActiveSource UpdateLocked(bool micActive, bool loopActive)
        {
            var lockedIsActive = _lockedSource == ActiveSource.Mic ? micActive : loopActive;
            var otherIsActive = _lockedSource == ActiveSource.Mic ? loopActive : micActive;

            // 打断检测：锁定方沉默 + 对方持续有声，且过了切换冷却期
            if (!lockedIsActive && otherIsActive)
            {
                _interruptionCount++;
                if (_interruptionCount >= _interruptionThreshold &&
                    _framesSinceLastSwitch >= _switchCooldownChunks)
                {
                    var other = _lockedSource == ActiveSource.Mic
                        ? ActiveSource.Loopback
                        : ActiveSource.Mic;
                    _log?.Invoke($"[VAD门控] 打断检测触发，从 {_lockedSource} 切换到 {other}");
                    Unlock();
                    return Lock(other);
                }
            }
            else
            {
                _interruptionCount = 0;
            }

            // 安全阀：两方都长时间静音
            if (!micActive && !loopActive)
            {
                _silentChunks++;
                if (_silentChunks >= _safetyValveChunks)
                {
                    _log?.Invoke($"[VAD门控] 安全阀触发，长时间静音解锁 (来源={_lockedSource})");
                    Unlock();
                    return ActiveSource.None;
                }
            }
            else
            {
                _silentChunks = 0;
            }

            return _lockedSource;
        }

        private ActiveSource UpdateUnlocked(bool micActive, bool loopActive)
        {
            // 两路都静音
            if (!micActive && !loopActive)
            {
                ResetCandidate();
                return ActiveSource.None;
            }

            // 选出候选源
            ActiveSource candidate;
            if (micActive && !loopActive)
            {
                candidate = ActiveSource.Mic;
            }
            else if (loopActive && !micActive)
            {
                candidate = ActiveSource.Loopback;
            }
            else
            {
                // 两方同时有声 → 仲裁
                candidate = ArbitrateBothActive();
            }

            // 最小说话防抖：候选源必须连续 N 帧才真正锁
            if (_candidateSource == candidate)
            {
                _candidateChunks++;
            }
            else
            {
                _candidateSource = candidate;
                _candidateChunks = 1;
            }

            if (_candidateChunks >= _minSpeechChunks)
            {
                return Lock(candidate);
            }

            return ActiveSource.None;
        }

        private ActiveSource ArbitrateBothActive()
        {
            switch (_arbitrationMode)
            {
                case VadArbitrationMode.Loudest:
                    return _micEma >= _loopEma ? ActiveSource.Mic : ActiveSource.Loopback;

                case VadArbitrationMode.LoudestWithMargin:
                    var diffDb = RmsToDb(_micEma) - RmsToDb(_loopEma);
                    if (diffDb >= _tieMarginDb) return ActiveSource.Mic;
                    if (-diffDb >= _tieMarginDb) return ActiveSource.Loopback;
                    return _conflictPriority;

                case VadArbitrationMode.StaticPriority:
                default:
                    return _conflictPriority;
            }
        }

        private ActiveSource Lock(ActiveSource source)
        {
            _lockedSource = source;
            _interruptionCount = 0;
            _silentChunks = 0;
            ResetCandidate();
            _log?.Invoke($"[VAD门控] 锁定 → {source}");
            return source;
        }

        private void Unlock()
        {
            _lockedSource = ActiveSource.None;
            _interruptionCount = 0;
            _silentChunks = 0;
            ResetCandidate();
        }

        private void ResetCandidate()
        {
            _candidateSource = ActiveSource.None;
            _candidateChunks = 0;
        }

        private static double RmsToDb(double rms)
        {
            if (rms <= 1e-6) return -120.0;
            return 20.0 * Math.Log10(rms);
        }

        /// <summary>计算 PCM16 单声道数据的 RMS（归一化到 0~1）。</summary>
        public static double ComputeRmsPcm16(byte[] pcm16)
        {
            if (pcm16.Length < 2)
            {
                return 0;
            }

            long sumSq = 0;
            var count = pcm16.Length / 2;
            for (var i = 0; i < count; i++)
            {
                var idx = i * 2;
                var sample = (short)(pcm16[idx] | (pcm16[idx + 1] << 8));
                sumSq += (long)sample * sample;
            }

            return Math.Sqrt(sumSq / (double)count) / 32768.0;
        }
    }
}
