namespace TrueFluentPro.Models
{
    /// <summary>
    /// 图片/视频生成的全局配置（持久化到 config.json）。
    /// 纯数据容器（POCO），不承担属性变更通知。
    /// Settings 用本地字段编辑后写回；Session 在构造时快照默认值。
    /// </summary>
    public class MediaGenConfig
    {
        // --- 终结点模型引用 ---
        public ModelReference? ImageModelRef { get; set; }
        public ModelReference? VideoModelRef { get; set; }

        // --- 图片默认参数 ---
        public string ImageModel { get; set; } = "gpt-image-1";
        public string ImageSize { get; set; } = "1024x1024";
        public string ImageQuality { get; set; } = "medium";
        public string ImageFormat { get; set; } = "png";
        public int ImageCount { get; set; } = 1;
        public ImageEditMode ImageEditMode { get; set; } = ImageEditMode.V2ResponsesApi;

        /// <summary>
        /// 聊天模式下是否启用图片生成工具（image_generation tool）。
        /// 启用后，聊天中 AI 可自主判断是否需要生成/编辑图片。
        /// 需要模型具有视觉能力（如 gpt-4o/gpt-5.4），且需配置 ImageModel。
        /// </summary>
        public bool EnableChatImageGeneration { get; set; } = true;

        // --- 视频默认参数 ---
        public string VideoModel { get; set; } = "sora-2";
        public VideoApiMode VideoApiMode { get; set; } = VideoApiMode.Videos;
        public int VideoWidth { get; set; } = 1280;
        public int VideoHeight { get; set; } = 720;
        public string VideoAspectRatio { get; set; } = "16:9";
        public string VideoResolution { get; set; } = "720p";
        public int VideoSeconds { get; set; } = 4;
        public int VideoVariants { get; set; } = 1;
        public int VideoPollIntervalMs { get; set; } = 3000;

        // --- 创作工坊文本会话默认值 ---
        public bool DefaultEnableStudioReasoning { get; set; } = false;
        public bool DefaultEnableStudioWebSearch { get; set; } = false;

        // --- 会话上下文 ---
        /// <summary>聊天模式：每次请求携带的最大历史轮数（全局默认值，会话级可覆盖）</summary>
        public int DefaultMaxConversationTurns { get; set; } = 20;

        // --- 运行时注入（不持久化）---
        /// <summary>
        /// V2 图片编辑 Responses API 使用的文本模型名（如 gpt-4o / gpt-5.4）。
        /// 由 ViewModel 在调用前从聊天模型运行时注入，不写入 config.json。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? TextModelForResponses { get; set; }

        // --- 性能与缓存 ---
        public int MaxLoadedSessionsInMemory { get; set; } = 8;

        // --- 输出 ---
        public string OutputDirectory { get; set; } = "";
    }
}
