using System;

namespace TrueFluentPro.Services
{
    /// <summary>仅用于浮窗的句快照；同句的实时修订与终稿共用本地编号。</summary>
    public sealed record FloatingSubtitleUpdate(long SegmentId, string Text, bool IsFinal,
        Audio.VadGateController.ActiveSource Source);

    public class SubtitleSyncService
    {
        public event Action<string>? SubtitleUpdated;
        public event Action<FloatingSubtitleUpdate>? SubtitleFrameUpdated;
        /// <summary>争抢窗口开/关变化通知（边沿触发）。</summary>
        public event Action<bool>? ContestStateChanged;
        private string _currentSubtitle = "";
        private bool _isContestActive;
        private long _nextSyntheticSegmentId;
        private FloatingSubtitleUpdate? _activeSegment;

        /// <summary>最近一次字幕更新所属的说话源（供悬浮窗动态切话人图标）。</summary>
        public Audio.VadGateController.ActiveSource CurrentSource { get; private set; }
            = Audio.VadGateController.ActiveSource.None;

        /// <summary>当前是否处于"争抢窗口"或"双输出"——浮动字幕可据此显示副轨提示。</summary>
        public bool IsContestActive
        {
            get => _isContestActive;
            private set
            {
                if (_isContestActive != value)
                {
                    _isContestActive = value;
                    ContestStateChanged?.Invoke(value);
                }
            }
        }

        public void UpdateContestState(bool active)
        {
            IsContestActive = active;
        }

        public string CurrentSubtitle
        {
            get => _currentSubtitle;
            private set
            {
                if (_currentSubtitle != value)
                {
                    _currentSubtitle = value;
                    SubtitleUpdated?.Invoke(value);
                }
            }
        }

        public void UpdateSubtitle(string subtitle)
        {
            // 状态提示使用负编号，不会与主翻译事件的正编号冲突。
            UpdateSubtitle(new FloatingSubtitleUpdate(--_nextSyntheticSegmentId, subtitle ?? "", true,
                Audio.VadGateController.ActiveSource.None));
        }

        public void UpdateSubtitle(string subtitle, Audio.VadGateController.ActiveSource source, bool isFinal = false)
        {
            var segmentId = _activeSegment is { IsFinal: false } previous
                ? previous.SegmentId
                : --_nextSyntheticSegmentId;
            UpdateSubtitle(new FloatingSubtitleUpdate(segmentId, subtitle ?? "", isFinal, source));
        }

        public void UpdateSubtitle(FloatingSubtitleUpdate update)
        {
            _activeSegment = update;
            CurrentSource = update.Source;
            // 即使文本未变但说话人变了，也需要通知 VM 刷新图标。
            if (_currentSubtitle == update.Text)
            {
                SubtitleUpdated?.Invoke(_currentSubtitle);
            }
            else
            {
                CurrentSubtitle = update.Text;
            }

            // 终稿即使文字未变也需要通知分页器；来源只负责显示，不决定句子身份。
            SubtitleFrameUpdated?.Invoke(update);
        }

        public void ClearSubtitle()
        {
            _activeSegment = null;
            UpdateSubtitle("");
        }
    }
}

