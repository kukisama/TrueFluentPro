namespace TrueFluentPro.Models
{
    /// <summary>
    /// 图片/视频生成的全局配置（持久化到 config.json）。
    /// 纯数据容器（POCO），不承担属性变更通知。
    /// Settings 用本地字段编辑后写回；Session 在构造时快照默认值。
    /// </summary>
    public class MediaGenConfig
    {
        // --- 图片终结点配置 ---
        public AiProviderType ImageProviderType { get; set; } = AiProviderType.OpenAiCompatible;
        public string ImageApiEndpoint { get; set; } = "";
        public string ImageApiKey { get; set; } = "";
        public AzureAuthMode ImageAzureAuthMode { get; set; } = AzureAuthMode.ApiKey;
        public string ImageAzureTenantId { get; set; } = "";
        public string ImageAzureClientId { get; set; } = "";

        // --- 视频终结点配置 ---
        public AiProviderType VideoProviderType { get; set; } = AiProviderType.OpenAiCompatible;
        public string VideoApiEndpoint { get; set; } = "";
        public string VideoApiKey { get; set; } = "";
        public AzureAuthMode VideoAzureAuthMode { get; set; } = AzureAuthMode.ApiKey;
        public string VideoAzureTenantId { get; set; } = "";
        public string VideoAzureClientId { get; set; } = "";

        // --- 终结点模型引用（3.4 新增，与旧字段并存以便迁移） ---
        public ModelReference? ImageModelRef { get; set; }
        public ModelReference? VideoModelRef { get; set; }

        // --- 图片默认参数 ---
        public string ImageModel { get; set; } = "gpt-image-1";
        public string ImageSize { get; set; } = "1024x1024";
        public string ImageQuality { get; set; } = "medium";
        public string ImageFormat { get; set; } = "png";
        public int ImageCount { get; set; } = 1;

        // --- 视频默认参数 ---
        public string VideoModel { get; set; } = "sora-2";
        public VideoApiMode VideoApiMode { get; set; } = VideoApiMode.SoraJobs;
        public int VideoWidth { get; set; } = 854;
        public int VideoHeight { get; set; } = 480;
        public string VideoAspectRatio { get; set; } = "16:9";
        public string VideoResolution { get; set; } = "480p";
        public int VideoSeconds { get; set; } = 5;
        public int VideoVariants { get; set; } = 1;
        public int VideoPollIntervalMs { get; set; } = 3000;

        // --- 性能与缓存 ---
        public int MaxLoadedSessionsInMemory { get; set; } = 8;

        // --- 输出 ---
        public string OutputDirectory { get; set; } = "";
    }
}
