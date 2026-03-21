using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TrueFluentPro.Models
{
    public enum MediaGenType
    {
        Text,
        Image,
        Video
    }

    public enum MediaGenStatus
    {
        Queued,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// 单个生成任务（图片或视频）
    /// </summary>
    public class MediaGenTask : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public MediaGenType Type { get; set; }

        private MediaGenStatus _status;
        public MediaGenStatus Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string Prompt { get; set; } = "";

        /// <summary>
        /// 创建任务时是否带参考输入，用于区分“创建”还是“修改 / 图生视频”。
        /// </summary>
        public bool HasReferenceInput { get; set; }

        private int _progress;
        public int Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(); }
        }

        private string? _resultFilePath;
        public string? ResultFilePath
        {
            get => _resultFilePath;
            set { _resultFilePath = value; OnPropertyChanged(); }
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            set { _errorMessage = value; OnPropertyChanged(); }
        }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // 视频专用
        public string? RemoteVideoId { get; set; }

        /// <summary>
        /// 记录创建该视频任务时使用的视频 API 模式（用于恢复时选择正确的端点）。
        /// </summary>
        public VideoApiMode? RemoteVideoApiMode { get; set; }

        /// <summary>
        /// 轮询拿到的 generationId（gen_...），用于下载。
        /// 若已有此值则恢复任务时可跳过轮询直接下载。
        /// </summary>
        public string? RemoteGenerationId { get; set; }

        /// <summary>服务端生成耗时（秒）：从创建到 succeeded</summary>
        public double? GenerateSeconds { get; set; }

        /// <summary>下载传输耗时（秒）：从 succeeded 到文件写完</summary>
        public double? DownloadSeconds { get; set; }

        /// <summary>实际下载成功的完整 URL（用于排查和恢复）</summary>
        public string? RemoteDownloadUrl { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
