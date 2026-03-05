using System;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 描述一个可用的远程更新版本。
    /// </summary>
    public sealed class UpdateInfo
    {
        public Version LatestVersion { get; init; } = new();
        public string DownloadUrl { get; init; } = "";
        public string ReleasePageUrl { get; init; } = "";
        public string ReleaseNotes { get; init; } = "";
        /// <summary>
        /// GitHub API 返回的 asset 文件大小（字节），用于下载缓存校验。
        /// </summary>
        public long AssetSize { get; init; }
    }
}
