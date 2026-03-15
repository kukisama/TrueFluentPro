using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 会话模型（包含聊天记录和任务列表）
    /// </summary>
    public class MediaGenSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "新会话";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool IsDeleted { get; set; } = false;
        public string CanvasMode { get; set; } = "Draw";
        public string MediaKind { get; set; } = "Image";
        public List<MediaChatMessage> Messages { get; set; } = new();
        public List<MediaGenTask> Tasks { get; set; } = new();
    }

    /// <summary>
    /// 聊天消息
    /// </summary>
    public class MediaChatMessage
    {
        public string Role { get; set; } = "user";  // user / assistant / system
        public string Text { get; set; } = "";
        public List<string> MediaPaths { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>服务端生成耗时（秒）</summary>
        public double? GenerateSeconds { get; set; }
        /// <summary>下载传输耗时（秒）</summary>
        public double? DownloadSeconds { get; set; }
    }
}
