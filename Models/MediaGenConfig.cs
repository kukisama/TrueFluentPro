using CommunityToolkit.Mvvm.ComponentModel;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 图片/视频生成的全局配置（持久化到 config.json）。
    /// 继承 ObservableObject，图片/视频参数属性可被多个 ViewModel 共享并自动同步。
    /// </summary>
    public class MediaGenConfig : ObservableObject
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

        // --- 图片默认参数（Observable，Settings ↔ MediaStudio 共享） ---
        public string ImageModel { get; set; } = "gpt-image-1";

        private string _imageSize = "1024x1024";
        public string ImageSize { get => _imageSize; set => SetProperty(ref _imageSize, value); }

        private string _imageQuality = "medium";
        public string ImageQuality { get => _imageQuality; set => SetProperty(ref _imageQuality, value); }

        private string _imageFormat = "png";
        public string ImageFormat { get => _imageFormat; set => SetProperty(ref _imageFormat, value); }

        private int _imageCount = 1;
        public int ImageCount { get => _imageCount; set => SetProperty(ref _imageCount, value); }

        // --- 视频默认参数（Observable，Settings ↔ MediaStudio 共享） ---
        public string VideoModel { get; set; } = "sora-2";
        public VideoApiMode VideoApiMode { get; set; } = VideoApiMode.SoraJobs;
        public int VideoWidth { get; set; } = 854;
        public int VideoHeight { get; set; } = 480;

        private string _videoAspectRatio = "16:9";
        public string VideoAspectRatio { get => _videoAspectRatio; set => SetProperty(ref _videoAspectRatio, value); }

        private string _videoResolution = "480p";
        public string VideoResolution { get => _videoResolution; set => SetProperty(ref _videoResolution, value); }

        private int _videoSeconds = 5;
        public int VideoSeconds { get => _videoSeconds; set => SetProperty(ref _videoSeconds, value); }

        private int _videoVariants = 1;
        public int VideoVariants { get => _videoVariants; set => SetProperty(ref _videoVariants, value); }

        public int VideoPollIntervalMs { get; set; } = 3000;

        // --- 性能与缓存 ---
        public int MaxLoadedSessionsInMemory { get; set; } = 8;

        // --- 输出 ---
        public string OutputDirectory { get; set; } = "";
    }
}
