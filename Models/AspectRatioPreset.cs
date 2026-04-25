namespace TrueFluentPro.Models
{
    /// <summary>
    /// 图片比例预设（用于输入栏底部的比例选择器 Flyout）。
    /// </summary>
    public sealed class AspectRatioPreset
    {
        /// <summary>比例标签，如 "1:1"</summary>
        public required string Ratio { get; init; }

        /// <summary>对应的像素尺寸，如 "1024x1024"</summary>
        public required string Size { get; init; }

        /// <summary>中文友好描述，如 "头像、社交封面"</summary>
        public required string Description { get; init; }

        /// <summary>比例缩略图符号（Unicode 矩形字符）</summary>
        public required string Icon { get; init; }

        /// <summary>显示文本：比例 + 描述</summary>
        public string DisplayText => $"{Ratio}  {Description}";

        public static readonly AspectRatioPreset[] DefaultPresets =
        [
            new() { Ratio = "1:1",  Size = "1024x1024", Description = "头像、社交封面",   Icon = "◻" },
            new() { Ratio = "2:3",  Size = "1024x1536", Description = "社交媒体、自拍",   Icon = "▯" },
            new() { Ratio = "3:2",  Size = "1536x1024", Description = "摄影作品、风景",   Icon = "▭" },
            new() { Ratio = "3:4",  Size = "1024x1365", Description = "证件照、人像",     Icon = "▯" },
            new() { Ratio = "4:3",  Size = "1365x1024", Description = "文章配图、插画",   Icon = "▭" },
            new() { Ratio = "9:16", Size = "1152x2048", Description = "手机壁纸、人像",   Icon = "▮" },
            new() { Ratio = "16:9", Size = "1920x1080", Description = "桌面壁纸、风景",   Icon = "▬" },
        ];
    }
}
