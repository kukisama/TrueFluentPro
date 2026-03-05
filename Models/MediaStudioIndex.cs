using System;
using System.Collections.Generic;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// Media Studio 会话索引（轻量清单）。
    /// 用于快速加载会话列表，避免每次启动都遍历并反序列化全部会话内容。
    /// </summary>
    public class MediaStudioIndex
    {
        public int Version { get; set; } = 1;
        public int NextSessionNumber { get; set; } = 1;
        public List<MediaSessionIndexItem> Sessions { get; set; } = new();
    }

    /// <summary>
    /// 单个会话在索引中的轻量信息。
    /// </summary>
    public class MediaSessionIndexItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "新会话";
        public string DirectoryName { get; set; } = string.Empty;
        public bool IsDeleted { get; set; } = false;
        public int MessageCount { get; set; }
        public int TaskCount { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
