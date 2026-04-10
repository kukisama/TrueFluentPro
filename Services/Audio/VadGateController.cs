using System;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 句级 VAD 门控：双路音频（麦克风 + 环回）场景下，
    /// 根据 RMS 能量判断谁在说话，句级锁定后只送一路给识别器。
    /// </summary>
    public sealed class VadGateController
    {
        public enum ActiveSource { None, Mic, Loopback }

        private readonly double _voiceThreshold;
        private readonly int _interruptionThreshold;
        private readonly int _safetyValveChunks;
        private readonly ActiveSource _conflictPriority;
        private readonly Action<string>? _log;

        private ActiveSource _lockedSource = ActiveSource.None;
        private int _interruptionCount;
        private int _silentChunks;

        public ActiveSource Current => _lockedSource;

        public bool IsLocked => _lockedSource != ActiveSource.None;

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
            Action<string>? log = null)
        {
            _voiceThreshold = Math.Clamp(voiceThreshold, 0.001, 0.5);
            _interruptionThreshold = Math.Clamp(interruptionThreshold, 1, 50);
            _safetyValveChunks = Math.Clamp(safetyValveChunks, 5, 100);
            _conflictPriority = conflictPriority == ActiveSource.None ? ActiveSource.Loopback : conflictPriority;
            _log = log;
        }

        /// <summary>
        /// 每个 audio chunk 调用一次，返回本次应送给识别器的来源。
        /// </summary>
        public ActiveSource Update(double micRms, double loopbackRms)
        {
            var micActive = micRms >= _voiceThreshold;
            var loopActive = loopbackRms >= _voiceThreshold;

            if (IsLocked)
            {
                return UpdateLocked(micActive, loopActive);
            }

            return UpdateUnlocked(micActive, loopActive);
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
        }

        private ActiveSource UpdateLocked(bool micActive, bool loopActive)
        {
            var lockedIsActive = _lockedSource == ActiveSource.Mic ? micActive : loopActive;
            var otherIsActive = _lockedSource == ActiveSource.Mic ? loopActive : micActive;

            // 打断检测：锁定方沉默 + 对方持续有声
            if (!lockedIsActive && otherIsActive)
            {
                _interruptionCount++;
                if (_interruptionCount >= _interruptionThreshold)
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
                return ActiveSource.None;
            }

            // 只有一方有声
            if (micActive && !loopActive)
            {
                return Lock(ActiveSource.Mic);
            }

            if (loopActive && !micActive)
            {
                return Lock(ActiveSource.Loopback);
            }

            // 两方同时有声 → 按优先级
            return Lock(_conflictPriority);
        }

        private ActiveSource Lock(ActiveSource source)
        {
            _lockedSource = source;
            _interruptionCount = 0;
            _silentChunks = 0;
            _log?.Invoke($"[VAD门控] 锁定 → {source}");
            return source;
        }

        private void Unlock()
        {
            _lockedSource = ActiveSource.None;
            _interruptionCount = 0;
            _silentChunks = 0;
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
