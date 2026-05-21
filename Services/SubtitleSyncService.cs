using System;
using System.ComponentModel;

namespace TrueFluentPro.Services
{
    public class SubtitleSyncService
    {
        public event Action<string>? SubtitleUpdated;
        private string _currentSubtitle = "";

        /// <summary>最近一次字幕更新所属的说话源（供悬浮窗动态切话人图标）。</summary>
        public Audio.VadGateController.ActiveSource CurrentSource { get; private set; }
            = Audio.VadGateController.ActiveSource.None;

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
            CurrentSubtitle = subtitle ?? "";
        }

        public void UpdateSubtitle(string subtitle, Audio.VadGateController.ActiveSource source)
        {
            CurrentSource = source;
            // 即使文本未变但说话人变了，也需要通知 VM 刷新图标。
            if (_currentSubtitle == (subtitle ?? ""))
            {
                SubtitleUpdated?.Invoke(_currentSubtitle);
            }
            else
            {
                CurrentSubtitle = subtitle ?? "";
            }
        }

        public void ClearSubtitle()
        {
            CurrentSubtitle = "";
        }
    }
}

