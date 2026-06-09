using System;
using System.Collections.Generic;

namespace TrueFluentPro.Services.Audio
{
    /// <summary>
    /// 时间轴上的发言人采样点（一帧一个）。
    /// </summary>
    public readonly record struct ActiveSpeakerSample(
        DateTime Timestamp,
        VadGateController.ActiveSource ActiveSource,
        double MicRms,
        double LoopbackRms,
        double MicEmaRms,
        double LoopbackEmaRms,
        bool MicActive,
        bool LoopbackActive,
        bool IsLocked);

    /// <summary>
    /// 环形缓冲，存最近一段时间内的发言人采样，供时间轴 UI 渲染与字幕事后归属投票。
    /// 线程安全（写入由音频线程，读取由 UI 线程）。
    /// </summary>
    public sealed class ActiveSpeakerTimelineStore
    {
        private readonly object _lock = new();
        private readonly ActiveSpeakerSample[] _ring;
        private int _head; // 下一个写入位
        private int _count;

        /// <summary>容量（采样点数）。按 200ms/chunk、30 分钟算 ≈ 9000。</summary>
        public int Capacity => _ring.Length;

        public event EventHandler? SampleAdded;

        public ActiveSpeakerTimelineStore(int capacity = 9000)
        {
            if (capacity < 16) capacity = 16;
            _ring = new ActiveSpeakerSample[capacity];
        }

        public void Append(ActiveSpeakerSample sample)
        {
            lock (_lock)
            {
                _ring[_head] = sample;
                _head = (_head + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }
            SampleAdded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>按时间窗口拉取（含 start，含 end，按时间升序）。</summary>
        public List<ActiveSpeakerSample> GetRange(DateTime start, DateTime end)
        {
            var list = new List<ActiveSpeakerSample>();
            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    var idx = (_head - _count + i + _ring.Length) % _ring.Length;
                    var s = _ring[idx];
                    if (s.Timestamp >= start && s.Timestamp <= end)
                    {
                        list.Add(s);
                    }
                }
            }
            return list;
        }

        /// <summary>取最近 N 个采样（按时间升序）。</summary>
        public List<ActiveSpeakerSample> GetLast(int n)
        {
            var list = new List<ActiveSpeakerSample>();
            lock (_lock)
            {
                var take = Math.Min(n, _count);
                for (int i = 0; i < take; i++)
                {
                    var idx = (_head - take + i + _ring.Length) % _ring.Length;
                    list.Add(_ring[idx]);
                }
            }
            return list;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// 给定时间段内"锁定时间最长"的说话人，用于字幕事后归属投票。
        /// 没有任何锁定样本时返回 null。
        /// </summary>
        public VadGateController.ActiveSource? VoteDominantSpeaker(DateTime start, DateTime end)
        {
            int micCount = 0, loopCount = 0;
            lock (_lock)
            {
                for (int i = 0; i < _count; i++)
                {
                    var idx = (_head - _count + i + _ring.Length) % _ring.Length;
                    var s = _ring[idx];
                    if (s.Timestamp < start || s.Timestamp > end) continue;
                    if (!s.IsLocked) continue;
                    if (s.ActiveSource == VadGateController.ActiveSource.Mic) micCount++;
                    else if (s.ActiveSource == VadGateController.ActiveSource.Loopback) loopCount++;
                }
            }
            if (micCount == 0 && loopCount == 0) return null;
            return micCount >= loopCount
                ? VadGateController.ActiveSource.Mic
                : VadGateController.ActiveSource.Loopback;
        }
    }
}
