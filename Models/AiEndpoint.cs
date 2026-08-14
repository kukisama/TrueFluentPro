using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueFluentPro.Models.EndpointProfiles;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Models
{
    /// <summary>模型能力标签（可组合）</summary>
    [Flags]
    public enum ModelCapability
    {
        None = 0,
        Text = 1,              // 文字对话：洞察、复盘、快问
        Image = 2,             // 图片生成
        Video = 4,             // 视频生成
        SpeechToText = 8,      // 语音转文字 / 音频转写
        TextToSpeech = 16      // 文字转语音 / 语音合成
    }

    /// <summary>一个 AI 终结点（Provider 实例）</summary>
    public class AiEndpoint : ObservableObject
    {
        private string _id = "";
        private string _profileId = "";
        private string _name = "";
        private bool _isEnabled = true;
        private EndpointApiType _endpointType = EndpointApiType.OpenAiCompatible;
        private AiProviderType _providerType = AiProviderType.OpenAiCompatible;
        private string _baseUrl = "";
        private string _apiKey = "";
        private string _apiVersion = "";
        private AzureAuthMode _authMode = AzureAuthMode.ApiKey;
        private ApiKeyHeaderMode _apiKeyHeaderMode = ApiKeyHeaderMode.Auto;
        private TextApiProtocolMode _textApiProtocolMode = TextApiProtocolMode.Auto;
        private ImageApiRouteMode _imageApiRouteMode = ImageApiRouteMode.Auto;
        private string _azureTenantId = "";
        private string _azureClientId = "";
        private List<AiModelEntry> _models = new();

        // --- Azure Speech 专属字段 ---
        private string _speechSubscriptionKey = "";
        private string _speechRegion = "";
        private string _speechEndpoint = "";
        private SpeechCapability _speechCapabilities = SpeechCapability.RealtimeSpeechToText | SpeechCapability.BatchSpeechToText;

        public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string ProfileId { get => _profileId; set => SetProperty(ref _profileId, value); }
        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

        public EndpointApiType EndpointType
        {
            get => _endpointType;
            set
            {
                if (SetProperty(ref _endpointType, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                    OnPropertyChanged(nameof(EndpointTypeDisplayName));
                    OnPropertyChanged(nameof(EndpointTypeGlyph));
                    OnPropertyChanged(nameof(EndpointTypeMonogram));
                    OnPropertyChanged(nameof(EndpointTypeBadgeBackground));
                    OnPropertyChanged(nameof(EndpointTypeSubtitle));
                    OnPropertyChanged(nameof(EndpointTypeIconAssetPath));
                    OnPropertyChanged(nameof(IsSpeechEndpoint));
                    OnPropertyChanged(nameof(IsXunfeiRtasrEndpoint));
                    OnPropertyChanged(nameof(IsBaiduRealtimeAsrEndpoint));
                    OnPropertyChanged(nameof(IsThirdPartyRealtimeSpeechEndpoint));
                    OnPropertyChanged(nameof(EffectiveTranslateVendor));
                    OnPropertyChanged(nameof(IsTranslateXunfei));
                    OnPropertyChanged(nameof(IsTranslateBaidu));
                    OnPropertyChanged(nameof(ShowTranslateCredentials));
                    OnPropertyChanged(nameof(AsrCredentialHint));
                    OnPropertyChanged(nameof(MtCredentialHint));
                }
            }
        }

        // --- 连接信息 ---
        public AiProviderType ProviderType
        {
            get => _providerType;
            set
            {
                if (SetProperty(ref _providerType, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                if (SetProperty(ref _baseUrl, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
        public string ApiVersion { get => _apiVersion; set => SetProperty(ref _apiVersion, value); }

        // --- 认证 ---
        public AzureAuthMode AuthMode
        {
            get => _authMode;
            set
            {
                if (SetProperty(ref _authMode, value))
                {
                    OnPropertyChanged(nameof(IsAzureEndpoint));
                }
            }
        }

        public ApiKeyHeaderMode ApiKeyHeaderMode
        {
            get => _apiKeyHeaderMode;
            set => SetProperty(ref _apiKeyHeaderMode, value);
        }

        public TextApiProtocolMode TextApiProtocolMode
        {
            get => _textApiProtocolMode;
            set => SetProperty(ref _textApiProtocolMode, value);
        }

        public ImageApiRouteMode ImageApiRouteMode
        {
            get => _imageApiRouteMode;
            set => SetProperty(ref _imageApiRouteMode, value);
        }

        public string AzureTenantId { get => _azureTenantId; set => SetProperty(ref _azureTenantId, value); }
        public string AzureClientId { get => _azureClientId; set => SetProperty(ref _azureClientId, value); }

        // --- Azure Speech 专属属性 ---
        public string SpeechSubscriptionKey { get => _speechSubscriptionKey; set => SetProperty(ref _speechSubscriptionKey, value); }
        public string SpeechRegion { get => _speechRegion; set => SetProperty(ref _speechRegion, value); }
        public string SpeechEndpoint
        {
            get => _speechEndpoint;
            set
            {
                if (SetProperty(ref _speechEndpoint, value))
                    OnPropertyChanged(nameof(SpeechRegionHint));
            }
        }
        public SpeechCapability SpeechCapabilities { get => _speechCapabilities; set => SetProperty(ref _speechCapabilities, value); }

        // --- 第三方实时语音厂商（讯飞 / 百度）凭据：ASR 与 MT 双块、各厂商独立存储，互不串值 ---
        // 设计：每个「积木」（识别 / 翻译 × 厂商）各自记录自己的凭据，切换厂商不会覆盖或丢失其它厂商已填内容。
        // 讯飞识别（RTASR）：AppId + ApiKey + ApiSecret（可选）。
        private string _xunfeiAsrAppId = "";
        private string _xunfeiAsrApiKey = "";
        private string _xunfeiAsrApiSecret = "";
        // 百度识别（实时语音）：AppId + AppKey。
        private string _baiduAsrAppId = "";
        private string _baiduAsrApiKey = "";
        // 讯飞翻译（NiuTrans）：AppId + ApiKey + ApiSecret。
        private string _xunfeiMtAppId = "";
        private string _xunfeiMtApiKey = "";
        private string _xunfeiMtApiSecret = "";
        // 百度翻译：AppId + 大模型 ApiKey（可选） + 通用 SecretKey（可选）。
        private string _baiduMtAppId = "";
        private string _baiduMtApiKey = "";
        private string _baiduMtSecretKey = "";
        private SpeechTranslationVendor _translateVendor = SpeechTranslationVendor.FollowAsr;

        public string XunfeiAsrAppId { get => _xunfeiAsrAppId; set => SetProperty(ref _xunfeiAsrAppId, value); }
        public string XunfeiAsrApiKey { get => _xunfeiAsrApiKey; set => SetProperty(ref _xunfeiAsrApiKey, value); }
        public string XunfeiAsrApiSecret { get => _xunfeiAsrApiSecret; set => SetProperty(ref _xunfeiAsrApiSecret, value); }
        public string BaiduAsrAppId { get => _baiduAsrAppId; set => SetProperty(ref _baiduAsrAppId, value); }
        public string BaiduAsrApiKey { get => _baiduAsrApiKey; set => SetProperty(ref _baiduAsrApiKey, value); }
        public string XunfeiMtAppId { get => _xunfeiMtAppId; set => SetProperty(ref _xunfeiMtAppId, value); }
        public string XunfeiMtApiKey { get => _xunfeiMtApiKey; set => SetProperty(ref _xunfeiMtApiKey, value); }
        public string XunfeiMtApiSecret { get => _xunfeiMtApiSecret; set => SetProperty(ref _xunfeiMtApiSecret, value); }
        public string BaiduMtAppId { get => _baiduMtAppId; set => SetProperty(ref _baiduMtAppId, value); }
        public string BaiduMtApiKey { get => _baiduMtApiKey; set => SetProperty(ref _baiduMtApiKey, value); }
        public string BaiduMtSecretKey { get => _baiduMtSecretKey; set => SetProperty(ref _baiduMtSecretKey, value); }

        /// <summary>翻译厂商选择（ASR/MT 解耦）。仅第三方实时语音终结点使用。</summary>
        public SpeechTranslationVendor TranslateVendor
        {
            get => _translateVendor;
            set
            {
                if (SetProperty(ref _translateVendor, value))
                {
                    OnPropertyChanged(nameof(EffectiveTranslateVendor));
                    OnPropertyChanged(nameof(IsTranslateXunfei));
                    OnPropertyChanged(nameof(IsTranslateBaidu));
                    OnPropertyChanged(nameof(ShowTranslateCredentials));
                    OnPropertyChanged(nameof(MtCredentialHint));
                }
            }
        }

        /// <summary>实际生效的翻译厂商：FollowAsr / Llm 跟随识别同厂商；None 表示不翻译。</summary>
        [JsonIgnore]
        public SpeechTranslationVendor EffectiveTranslateVendor =>
            _translateVendor switch
            {
                SpeechTranslationVendor.Xunfei => SpeechTranslationVendor.Xunfei,
                SpeechTranslationVendor.Baidu => SpeechTranslationVendor.Baidu,
                SpeechTranslationVendor.None => SpeechTranslationVendor.None,
                _ => EndpointType switch // FollowAsr / Llm
                {
                    EndpointApiType.XunfeiRtasr => SpeechTranslationVendor.Xunfei,
                    EndpointApiType.BaiduRealtimeAsr => SpeechTranslationVendor.Baidu,
                    _ => SpeechTranslationVendor.None
                }
            };

        /// <summary>当前生效翻译厂商是否为讯飞（用于 MT 字段显隐）。</summary>
        [JsonIgnore]
        public bool IsTranslateXunfei => EffectiveTranslateVendor == SpeechTranslationVendor.Xunfei;

        /// <summary>当前生效翻译厂商是否为百度（用于 MT 字段显隐）。</summary>
        [JsonIgnore]
        public bool IsTranslateBaidu => EffectiveTranslateVendor == SpeechTranslationVendor.Baidu;

        /// <summary>是否需要展示机器翻译凭据字段（不翻译时隐藏）。</summary>
        [JsonIgnore]
        public bool ShowTranslateCredentials => EffectiveTranslateVendor != SpeechTranslationVendor.None;

        /// <summary>识别凭据填写提示——仅描述当前厂商自己，不提及其它厂商。</summary>
        [JsonIgnore]
        public string AsrCredentialHint => EndpointType switch
        {
            EndpointApiType.AzureSpeech => "微软 Azure 语音：填写订阅密钥与区域终结点。可在 Azure 门户的“语音服务”资源 →「密钥和终结点」页获取，点击“测试连接”可校验是否可用。",
            EndpointApiType.XunfeiRtasr => "讯飞实时语音转写（RTASR）：在讯飞开放平台创建语音应用后获取 AppId 与 ApiKey；ApiSecret 视服务类型可选。可点下方按钮直达讯飞控制台。",
            EndpointApiType.BaiduRealtimeAsr => "百度实时语音识别：在百度智能云语音应用中获取 AppId 与 API Key（AppKey）。可点下方按钮直达百度智能云控制台。",
            _ => ""
        };

        /// <summary>机器翻译凭据填写提示——仅描述当前生效翻译厂商。</summary>
        [JsonIgnore]
        public string MtCredentialHint => EffectiveTranslateVendor switch
        {
            SpeechTranslationVendor.Xunfei => "讯飞 NiuTrans 机器翻译：填写翻译用 AppId / ApiKey / ApiSecret；留空则实时翻译仅显示原文。",
            SpeechTranslationVendor.Baidu => "百度机器翻译：可填大模型 ApiKey（优先）或通用 SecretKey（回退），任一即可；留空则实时翻译仅显示原文。",
            _ => "当前未启用机器翻译，实时字幕仅显示识别原文。"
        };

        [JsonIgnore]
        public bool IsSpeechEndpoint => EndpointType == EndpointApiType.AzureSpeech;

        /// <summary>讯飞实时语音终结点</summary>
        [JsonIgnore]
        public bool IsXunfeiRtasrEndpoint => EndpointType == EndpointApiType.XunfeiRtasr;

        /// <summary>百度实时语音终结点</summary>
        [JsonIgnore]
        public bool IsBaiduRealtimeAsrEndpoint => EndpointType == EndpointApiType.BaiduRealtimeAsr;

        /// <summary>是否为第三方实时语音厂商（讯飞 / 百度）终结点</summary>
        [JsonIgnore]
        public bool IsThirdPartyRealtimeSpeechEndpoint =>
            EndpointType is EndpointApiType.XunfeiRtasr or EndpointApiType.BaiduRealtimeAsr;

        [JsonIgnore]
        public string SpeechRegionHint
        {
            get
            {
                var ep = SpeechEndpoint?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(ep)) return "";
                var region = AzureSubscription.ParseRegionFromEndpoint(ep);
                if (!string.IsNullOrWhiteSpace(region))
                {
                    var type = ep.Contains(".azure.cn", StringComparison.OrdinalIgnoreCase) ? "中国区" : "国际版";
                    return $"✓ 已识别区域: {region} ({type})";
                }
                return "✗ 无法识别区域，请检查终结点格式";
            }
        }

        /// <summary>
        /// 是否为 Azure OpenAI 终结点。
        /// 仅由终结点类型决定，不再基于域名或 ProviderType 猜测。
        /// </summary>
        [JsonIgnore]
        public bool IsAzureEndpoint => EndpointType == EndpointApiType.AzureOpenAi;

        /// <summary>
        /// 当前终结点类型对应的「展示资料包」。展示文案/图标已由 profile JSON 驱动——
        /// 新增厂商只需在 Assets/EndpointProfiles/Profiles 丢一个 JSON 即可，无需再改这里的代码。
        /// 资料包加载失败时为 null，下面各展示属性会回退到通用默认值。
        /// </summary>
        [JsonIgnore]
        private EndpointProfileDefinition? DisplayProfile
            => EndpointProfileCatalogService.GetDisplayProfile(EndpointType);

        [JsonIgnore]
        public string EndpointTypeDisplayName
            => Fallback(DisplayProfile?.DisplayName, "OpenAI Compatible");

        [JsonIgnore]
        public string EndpointTypeGlyph
            => Fallback(DisplayProfile?.Glyph, "◎");

        [JsonIgnore]
        public string EndpointTypeMonogram
            => Fallback(DisplayProfile?.Monogram, "OA");

        [JsonIgnore]
        public string EndpointTypeBadgeBackground
            => Fallback(DisplayProfile?.BadgeBackground, "#10A37F");

        [JsonIgnore]
        public string EndpointTypeSubtitle
            => Fallback(DisplayProfile?.Subtitle, "标准 OpenAI / 兼容服务");

        [JsonIgnore]
        public string EndpointTypeIconAssetPath
            => NormalizeAssetPath(DisplayProfile?.IconAssetPath, "/Assets/EndpointProfiles/Icons/openai-compatible.svg");

        private static string Fallback(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        // svg:Svg 的 Path 需要以「/」开头的资源路径；profile JSON 内存的是无前导斜杠的相对路径，这里统一补齐。
        private static string NormalizeAssetPath(string? value, string fallback)
            => string.IsNullOrWhiteSpace(value)
                ? fallback
                : "/" + value.TrimStart('/', '\\').Replace('\\', '/');

        // --- 模型列表 ---
        public List<AiModelEntry> Models { get => _models; set => SetProperty(ref _models, value); }

        /// <summary>
        /// 反序列化时捕获未知 / 历史字段（如旧版扁平凭据 AppId / ApiSecret / TranslateAppId /
        /// TranslateApiKey / TranslateApiSecret，以及历史误写入的只读计算属性）。
        /// 迁移完成后会被清空，避免脏字段回写配置文件。
        /// </summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }

        /// <summary>
        /// 将旧版扁平凭据迁移到按厂商独立存储的字段（幂等，可在每次加载时调用）。
        /// 旧版本第三方实时语音端点用 ApiKey 存识别密钥，并用扁平字段
        /// AppId / ApiSecret / TranslateAppId / TranslateApiKey / TranslateApiSecret 存其余凭据。
        /// 改版后这些字段拆分为各厂商独立属性，需在加载时回填，否则会“丢失”。
        /// </summary>
        public void MigrateLegacyCredentials()
        {
            string Legacy(string name) =>
                ExtensionData != null
                && ExtensionData.TryGetValue(name, out var el)
                && el.ValueKind == JsonValueKind.String
                    ? el.GetString() ?? "" : "";

            if (EndpointType == EndpointApiType.XunfeiRtasr)
            {
                var legacyAppId = Legacy("AppId");
                var legacyApiSecret = Legacy("ApiSecret");
                var legacyTransAppId = Legacy("TranslateAppId");
                var legacyTransApiKey = Legacy("TranslateApiKey");
                var legacyTransApiSecret = Legacy("TranslateApiSecret");

                if (string.IsNullOrWhiteSpace(_xunfeiAsrAppId)) _xunfeiAsrAppId = legacyAppId;
                if (string.IsNullOrWhiteSpace(_xunfeiAsrApiKey)) _xunfeiAsrApiKey = _apiKey;
                if (string.IsNullOrWhiteSpace(_xunfeiAsrApiSecret)) _xunfeiAsrApiSecret = legacyApiSecret;
                if (string.IsNullOrWhiteSpace(_xunfeiMtAppId)) _xunfeiMtAppId = legacyTransAppId;
                if (string.IsNullOrWhiteSpace(_xunfeiMtApiKey)) _xunfeiMtApiKey = legacyTransApiKey;
                if (string.IsNullOrWhiteSpace(_xunfeiMtApiSecret)) _xunfeiMtApiSecret = legacyTransApiSecret;
            }
            else if (EndpointType == EndpointApiType.BaiduRealtimeAsr)
            {
                var legacyAppId = Legacy("AppId");
                var legacyTransAppId = Legacy("TranslateAppId");
                var legacyTransApiKey = Legacy("TranslateApiKey");
                var legacyTransApiSecret = Legacy("TranslateApiSecret");

                if (string.IsNullOrWhiteSpace(_baiduAsrAppId)) _baiduAsrAppId = legacyAppId;
                if (string.IsNullOrWhiteSpace(_baiduAsrApiKey)) _baiduAsrApiKey = _apiKey;
                if (string.IsNullOrWhiteSpace(_baiduMtAppId)) _baiduMtAppId = legacyTransAppId;
                if (string.IsNullOrWhiteSpace(_baiduMtApiKey)) _baiduMtApiKey = legacyTransApiKey;
                if (string.IsNullOrWhiteSpace(_baiduMtSecretKey)) _baiduMtSecretKey = legacyTransApiSecret;
            }

            // 清空扩展数据，避免历史脏字段（含旧只读计算属性）回写到 config.json。
            ExtensionData = null;
        }
    }

    /// <summary>终结点下的一个模型定义</summary>
    public class AiModelEntry : ObservableObject
    {
        private string _modelId = "";
        private string _displayName = "";
        private string _deploymentName = "";
        private string _groupName = "";
        private ModelCapability _capabilities = ModelCapability.None;

        public string ModelId
        {
            get => _modelId;
            set
            {
                if (SetProperty(ref _modelId, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
        public string DeploymentName { get => _deploymentName; set => SetProperty(ref _deploymentName, value); }
        public string GroupName { get => _groupName; set => SetProperty(ref _groupName, value); }
        public ModelCapability Capabilities { get => _capabilities; set => SetProperty(ref _capabilities, value); }

        public string DisplayTitle => string.IsNullOrWhiteSpace(ModelId) ? "未命名模型" : ModelId;
    }

    /// <summary>功能分区对某个模型的引用（终结点ID + 模型ID）</summary>
    public class ModelReference
    {
        public string EndpointId { get; set; } = "";
        public string ModelId { get; set; } = "";
    }

    /// <summary>下拉框/卡片中的模型选项，显示 "终结点名 / 模型名"</summary>
    public class ModelOption
    {
        public ModelReference Reference { get; init; } = new();
        public string EndpointName { get; init; } = "";
        public string ModelDisplayName { get; init; } = "";
        public EndpointApiType EndpointType { get; init; } = EndpointApiType.OpenAiCompatible;
        public string ToolTipText => $"模型: {ModelDisplayName}\n终结点: {EndpointName}";
        public override string ToString() => $"{EndpointName} / {ModelDisplayName}";
    }
}
