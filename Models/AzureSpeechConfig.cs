using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using TrueFluentPro.Services;

namespace TrueFluentPro.Models
{
    public enum BatchLogLevel
    {
        Off,
        FailuresOnly,
        SuccessAndFailure
    }

    public enum AutoGainPreset
    {
        Off,
        Low,
        Medium,
        High
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ThemeModePreference
    {
        System,
        Light,
        Dark
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ReviewSubtitleSourceMode
    {
        DefaultSubtitle,
        SpeechSubtitle,
        AiTranscriptionSubtitle
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum WebSearchTriggerMode
    {
        Auto,
        Always
    }

    public class AzureSubscription
    {
        public string Name { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string ServiceRegion { get; set; } = "southeastasia";
        public string Endpoint { get; set; } = "";

        public bool IsValid()
        {
            return !string.IsNullOrEmpty(SubscriptionKey) &&
                   !string.IsNullOrEmpty(GetEffectiveRegion());
        }

        /// <summary>Get region from Endpoint, or fallback to ServiceRegion</summary>
        public string GetEffectiveRegion()
        {
            if (!string.IsNullOrWhiteSpace(Endpoint))
            {
                var parsed = ParseRegionFromEndpoint(Endpoint);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }
            return ServiceRegion;
        }

        /// <summary>Get the stored endpoint, or construct one from ServiceRegion for backward compat</summary>
        public string GetEffectiveEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(Endpoint))
                return Endpoint.TrimEnd('/');
            return $"https://{ServiceRegion}.api.cognitive.microsoft.com";
        }

        /// <summary>Whether this is a China Azure endpoint (.azure.cn)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsChinaEndpoint => GetEffectiveEndpoint().Contains(".azure.cn", StringComparison.OrdinalIgnoreCase);

        /// <summary>Get the cognitive services host base URL</summary>
        public string GetCognitiveServicesHost()
        {
            var region = GetEffectiveRegion();
            if (IsChinaEndpoint)
                return $"https://{region}.api.cognitive.azure.cn";
            return $"https://{region}.api.cognitive.microsoft.com";
        }

        /// <summary>Get the token issuing endpoint</summary>
        public string GetTokenEndpoint()
        {
            return $"{GetCognitiveServicesHost()}/sts/v1.0/issueToken";
        }

        /// <summary>Get the batch transcription API endpoint</summary>
        public string GetBatchTranscriptionEndpoint()
        {
            return $"{GetCognitiveServicesHost()}/speechtotext/v3.1/transcriptions";
        }

        /// <summary>Parse region from an endpoint URL</summary>
        public static string? ParseRegionFromEndpoint(string endpoint)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(endpoint)) return null;
                endpoint = endpoint.Trim();
                if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    endpoint = "https://" + endpoint;

                var uri = new Uri(endpoint);
                var host = uri.Host;

                // Pattern: {region}.api.cognitive.microsoft.com
                if (host.EndsWith(".api.cognitive.microsoft.com", StringComparison.OrdinalIgnoreCase))
                    return host.Replace(".api.cognitive.microsoft.com", "", StringComparison.OrdinalIgnoreCase);

                // Pattern: {region}.api.cognitive.azure.cn
                if (host.EndsWith(".api.cognitive.azure.cn", StringComparison.OrdinalIgnoreCase))
                    return host.Replace(".api.cognitive.azure.cn", "", StringComparison.OrdinalIgnoreCase);

                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    public class AzureSpeechConfig
    {
        public List<AzureSubscription> Subscriptions { get; set; } = new();
        public int ActiveSubscriptionIndex { get; set; } = 0;
        public List<SpeechResource> SpeechResources { get; set; } = new();
        public string ActiveSpeechResourceId { get; set; } = "";

        public ThemeModePreference ThemeMode { get; set; } = ThemeModePreference.System;
        public bool IsMainNavPaneOpen { get; set; } = false;

        public string SourceLanguage { get; set; } = "auto";
        public string TargetLanguage { get; set; } = "zh-Hans";

        public bool FilterModalParticles { get; set; } = true;
        public int MaxHistoryItems { get; set; } = 15;
        public int RealtimeMaxLength { get; set; } = 150;
        public bool EnableAutoTimeout { get; set; } = true;
        public int TimeoutSeconds { get; set; } = 5;
        public int InitialSilenceTimeoutSeconds { get; set; } = 25;
        public int EndSilenceTimeoutSeconds { get; set; } = 1;

        public bool EnableNoResponseRestart { get; set; } = false;
        public int NoResponseRestartSeconds { get; set; } = 3;
        public bool ShowReconnectMarkerInSubtitle { get; set; } = true;
        public int AudioActivityThreshold { get; set; } = 600;
        public double AudioLevelGain { get; set; } = 2.0;
        public bool AutoGainEnabled { get; set; } = false;
        public AutoGainPreset AutoGainPreset { get; set; } = AutoGainPreset.Off;
        public double AutoGainTargetRms { get; set; } = 0.12;
        public double AutoGainMinGain { get; set; } = 0.5;
        public double AutoGainMaxGain { get; set; } = 6.0;
        public double AutoGainSmoothing { get; set; } = 0.08;

        /// <summary>应用层音频预处理插件。主线路预留给 WebRTC APM 等可跨平台实现。</summary>
        public AudioPreProcessorPluginType AudioPreProcessorPlugin { get; set; } = AudioPreProcessorPluginType.None;

        /// <summary>WebRTC APM：回声消除。</summary>
        public bool WebRtcAecEnabled { get; set; } = true;
        /// <summary>WebRTC APM：是否使用移动端 AEC 模式。桌面默认 false。</summary>
        public bool WebRtcAecMobileMode { get; set; } = false;
        /// <summary>WebRTC APM：AEC 估计延迟（毫秒）。</summary>
        public int WebRtcAecLatencyMs { get; set; } = 40;
        /// <summary>WebRTC APM：降噪开关。</summary>
        public bool WebRtcNoiseSuppressionEnabled { get; set; } = true;
        /// <summary>WebRTC APM：降噪等级索引（0=Low,1=Moderate,2=High,3=VeryHigh）。</summary>
        public int WebRtcNoiseSuppressionLevel { get; set; } = 2;
        /// <summary>WebRTC APM：AGC1 开关。</summary>
        public bool WebRtcAgc1Enabled { get; set; } = true;
        /// <summary>WebRTC APM：AGC2 开关。</summary>
        public bool WebRtcAgc2Enabled { get; set; } = false;
        /// <summary>WebRTC APM：AGC 模式（0=AdaptiveAnalog,1=AdaptiveDigital,2=FixedDigital）。</summary>
        public int WebRtcAgcMode { get; set; } = 1;
        /// <summary>WebRTC APM：AGC 目标电平（dBFS）。</summary>
        public int WebRtcAgcTargetLevelDbfs { get; set; } = -3;
        /// <summary>WebRTC APM：AGC 压缩增益（dB）。</summary>
        public int WebRtcAgcCompressionGainDb { get; set; } = 9;
        /// <summary>WebRTC APM：AGC limiter 开关。</summary>
        public bool WebRtcAgcLimiterEnabled { get; set; } = true;
        /// <summary>WebRTC APM：高通滤波开关。</summary>
        public bool WebRtcHighPassFilterEnabled { get; set; } = true;
        /// <summary>WebRTC APM：前置放大开关。</summary>
        public bool WebRtcPreAmpEnabled { get; set; } = false;
        /// <summary>WebRTC APM：前置放大倍数。</summary>
        public float WebRtcPreAmpGain { get; set; } = 1.0f;

        /// <summary>启用 Microsoft Audio Stack (MAS) 识别增强（仅作用于云端识别支路）。需重启翻译生效。</summary>
        public bool EnableMasAudioProcessing { get; set; } = false;
        /// <summary>MAS 子开关：回声消除 (AEC)。仅 EnableMasAudioProcessing=true 时生效。</summary>
        public bool MasEchoCancellationEnabled { get; set; } = true;
        /// <summary>MAS 子开关：降噪 (NS)。仅 EnableMasAudioProcessing=true 时生效。</summary>
        public bool MasNoiseSuppressionEnabled { get; set; } = true;

        public AudioSourceMode AudioSourceMode { get; set; } = AudioSourceMode.DefaultMic;
        public string SelectedAudioDeviceId { get; set; } = "";
        public string SelectedOutputDeviceId { get; set; } = "";
        public RecordingMode RecordingMode { get; set; } = RecordingMode.LoopbackWithMic;
        public bool UseInputForRecognition { get; set; } = true;
        public bool UseOutputForRecognition { get; set; } = false;

        public int ChunkDurationMs { get; set; } = 200;

        // ── VAD 门控（双路说话人分离）──
        /// <summary>启用 VAD 门控（仅环回+麦克风双路模式下生效）。谁先说话整句归谁，被打断后自动切源。</summary>
        public bool EnableVadGating { get; set; } = true;
        /// <summary>RMS 低于此值视为静音（0~1，默认 0.01 ≈ -40dBFS）。</summary>
        public double VadVoiceThreshold { get; set; } = 0.01;
        /// <summary>锁定方沉默 + 对方有声连续多少 chunk 后判定打断（每 chunk ≈ ChunkDurationMs）。</summary>
        public int VadInterruptionChunks { get; set; } = 3;
        /// <summary>两路都静音超过多少 chunk 后强制解锁。</summary>
        public int VadSafetyValveChunks { get; set; } = 15;
        /// <summary>两人同时开口时优先谁：0=Loopback（对方），1=Mic（我）。</summary>
        public int VadConflictPriority { get; set; } = 0;

        public bool EnableRecording { get; set; } = true;
        public int RecordingMp3BitrateKbps { get; set; } = 256;
        public bool DeleteWavAfterMp3 { get; set; } = true;

        public bool ExportSrtSubtitles { get; set; } = false;
        public bool ExportVttSubtitles { get; set; } = true;

        public int DefaultFontSize { get; set; } = 38;

        public string? SessionDirectoryOverride { get; set; }

        public const string DefaultBatchAudioContainerName = "truefluentpro-audio";
        public const string DefaultBatchResultContainerName = "truefluentpro-results";

        public string BatchStorageConnectionString { get; set; } = "";
        public bool BatchStorageIsValid { get; set; } = false;
        public string BatchAudioContainerName { get; set; } = DefaultBatchAudioContainerName;
        public string BatchResultContainerName { get; set; } = DefaultBatchResultContainerName;
        public ReviewSubtitleSourceMode ReviewSubtitleSourceMode { get; set; } = ReviewSubtitleSourceMode.DefaultSubtitle;
        public bool UseSpeechSubtitleForReview { get; set; } = true;
        public BatchLogLevel BatchLogLevel { get; set; } = BatchLogLevel.Off;
        public bool BatchForceRegeneration { get; set; } = false;
        public bool ContextMenuForceRegeneration { get; set; } = true;
        public bool EnableBatchSubtitleSentenceSplit { get; set; } = true;
        public bool BatchSubtitleSplitOnComma { get; set; } = false;
        public int BatchSubtitleMaxChars { get; set; } = 24;
        public double BatchSubtitleMaxDurationSeconds { get; set; } = 6;
        public int BatchSubtitlePauseSplitMs { get; set; } = 500;

        public ModelReference? RealtimeTranscriptionModelRef { get; set; }
        public ModelReference? BatchTranscriptionModelRef { get; set; }
        public ModelReference? TextToSpeechModelRef { get; set; }

        public AiConfig? AiConfig { get; set; }

        public bool IsAutoUpdateEnabled { get; set; } = true;

        public MediaGenConfig MediaGenConfig { get; set; } = new();

        // ═══ 网页搜索 ═══
        public string WebSearchProviderId { get; set; } = "bing";
        public WebSearchTriggerMode WebSearchTriggerMode { get; set; } = WebSearchTriggerMode.Auto;
        public int WebSearchMaxResults { get; set; } = 5;
        public bool WebSearchEnableIntentAnalysis { get; set; } = true;
        public bool WebSearchEnableResultCompression { get; set; } = false;

        public bool WebSearchDebugMode { get; set; }

        // MCP 搜索配置
        public string WebSearchMcpEndpoint { get; set; } = "";
        public string WebSearchMcpToolName { get; set; } = "web_search";
        public string WebSearchMcpApiKey { get; set; } = "";

        /// <summary>统一 AI 终结点注册表</summary>
        public List<AiEndpoint> Endpoints { get; set; } = new();

        /// <summary>标记旧 Subscriptions 是否已迁移到 Endpoints（AzureSpeech 类型）</summary>
        public bool SpeechSubscriptionsMigratedToEndpoints { get; set; }

        [JsonIgnore]
        public string SessionDirectory => string.IsNullOrWhiteSpace(SessionDirectoryOverride)
            ? PathManager.Instance.SessionsPath
            : SessionDirectoryOverride;

        /// <summary>从所有启用的终结点中，按能力筛选可用模型</summary>
        public IEnumerable<(AiEndpoint Endpoint, AiModelEntry Model)>
            GetAvailableModels(ModelCapability required)
        {
            return Endpoints
                .Where(ep => ep.IsEnabled)
                .SelectMany(ep => ep.Models
                    .Where(m => m.Capabilities.HasFlag(required))
                    .Select(m => (ep, m)));
        }

        /// <summary>解析 ModelReference 为可用的终结点+模型</summary>
        public (AiEndpoint? Endpoint, AiModelEntry? Model)
            ResolveModel(ModelReference? reference)
        {
            if (reference == null) return (null, null);
            var ep = Endpoints.FirstOrDefault(e => e.Id == reference.EndpointId);
            var model = ep?.Models.FirstOrDefault(m => m.ModelId == reference.ModelId);
            return (ep, model);
        }

        public AzureSpeechConfig()
        {
        }

        public AzureSubscription? GetActiveSubscription()
        {
            if (Subscriptions.Count == 0) return null;

            if (ActiveSubscriptionIndex < 0 || ActiveSubscriptionIndex >= Subscriptions.Count)
            {
                ActiveSubscriptionIndex = 0;
            }

            return Subscriptions[ActiveSubscriptionIndex];
        }

        [JsonIgnore]
        public string SubscriptionKey
        {
            get => GetActiveSubscription()?.SubscriptionKey ?? "";
        }

        [JsonIgnore]
        public string ServiceRegion
        {
            get => GetActiveSubscription()?.GetEffectiveRegion() ?? "southeastasia";
        }

        public bool IsValid()
        {
            return CanStartRealtimeTranslation();
        }

        public bool CanStartRealtimeTranslation()
            => TryValidateRealtimeTranslationReadiness(out _);

        public bool TryValidateRealtimeTranslationReadiness(out string message)
        {
            if (string.IsNullOrWhiteSpace(SourceLanguage) || string.IsNullOrWhiteSpace(TargetLanguage))
            {
                message = "源语言或目标语言未配置。";
                return false;
            }

            return TryGetActiveRealtimeSpeechResource(out _, out message);
        }

        public ReviewSubtitleSourceMode GetEffectiveReviewSubtitleSourceMode()
        {
            var activeResource = GetActiveSpeechResource();
            if (activeResource == null || !activeResource.IsEnabled)
            {
                return ReviewSubtitleSourceMode.DefaultSubtitle;
            }

            return activeResource.ConnectorType switch
            {
                SpeechConnectorType.MicrosoftSpeech => ReviewSubtitleSourceMode.SpeechSubtitle,
                SpeechConnectorType.AiSpeech => ReviewSubtitleSourceMode.AiTranscriptionSubtitle,
                _ => ReviewSubtitleSourceMode.DefaultSubtitle
            };
        }

        public List<SpeechResource> GetEffectiveSpeechResources()
        {
            // 新体系：迁移完成后直接从 Endpoints 构建，不再依赖 SpeechResources 中间层
            if (SpeechSubscriptionsMigratedToEndpoints)
            {
                var result = new List<SpeechResource>();
                foreach (var ep in Endpoints.Where(ep => ep.EndpointType == EndpointApiType.AzureSpeech))
                {
                    var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                        ? ep.SpeechRegion
                        : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint ?? "") ?? "";

                    result.Add(new SpeechResource
                    {
                        Id = ep.Id,
                        Name = ep.Name,
                        Vendor = SpeechVendorType.Microsoft,
                        ConnectorType = SpeechConnectorType.MicrosoftSpeech,
                        IsEnabled = ep.IsEnabled,
                        Capabilities = ep.SpeechCapabilities != SpeechCapability.None
                            ? ep.SpeechCapabilities
                            : SpeechCapability.RealtimeSpeechToText | SpeechCapability.BatchSpeechToText,
                        SubscriptionName = ep.Name,
                        SubscriptionKey = ep.SpeechSubscriptionKey ?? "",
                        ServiceRegion = region,
                        Endpoint = ep.SpeechEndpoint ?? "",
                    });
                }

                // 保留非 Microsoft 资源（如 AiSpeech）
                result.AddRange(SpeechResources
                    .Where(r => r.ConnectorType != SpeechConnectorType.MicrosoftSpeech)
                    .Select(r => r.Clone()));

                return result;
            }

            // 旧体系回退（未迁移的配置）
            if (SpeechResources.Count > 0)
            {
                return SpeechResources.Select(resource => resource.Clone()).ToList();
            }

            var legacyResources = new List<SpeechResource>();
            for (var i = 0; i < Subscriptions.Count; i++)
            {
                legacyResources.Add(SpeechResource.CreateMicrosoftResource(Subscriptions[i], i));
            }

            var legacyAiResource = SpeechResource.CreateLegacyAiResource(
                RealtimeTranscriptionModelRef,
                BatchTranscriptionModelRef,
                TextToSpeechModelRef);
            if (legacyAiResource != null)
            {
                legacyResources.Add(legacyAiResource);
            }

            return legacyResources;
        }

        public SpeechResource? GetActiveSpeechResource()
        {
            var resources = GetEffectiveSpeechResources();
            if (resources.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(ActiveSpeechResourceId))
            {
                var exactMatch = resources.FirstOrDefault(resource =>
                    string.Equals(resource.Id, ActiveSpeechResourceId, StringComparison.OrdinalIgnoreCase));
                if (exactMatch != null)
                {
                    return exactMatch;
                }
            }

            if (SpeechResources.Count == 0)
            {
                if (ActiveSubscriptionIndex >= 0 && ActiveSubscriptionIndex < Subscriptions.Count)
                {
                    return resources.FirstOrDefault(resource =>
                        string.Equals(resource.Id, SpeechResource.BuildLegacyMicrosoftResourceId(ActiveSubscriptionIndex), StringComparison.OrdinalIgnoreCase));
                }

                return resources.FirstOrDefault();
            }

            return resources.FirstOrDefault();
        }

        public int GetEffectiveActiveSpeechResourceIndex()
        {
            var resources = GetEffectiveSpeechResources();
            if (resources.Count == 0)
            {
                return -1;
            }

            var activeResource = GetActiveSpeechResource();
            if (activeResource == null)
            {
                return -1;
            }

            return resources.FindIndex(resource =>
                string.Equals(resource.Id, activeResource.Id, StringComparison.OrdinalIgnoreCase));
        }

        public bool TryGetActiveRealtimeSpeechResource(out SpeechResource? resource, out string message)
        {
            resource = GetActiveSpeechResource();
            if (resource == null)
            {
                message = "未配置语音资源。";
                return false;
            }

            if (!resource.IsEnabled)
            {
                message = $"当前语音资源“{resource.Name}”已禁用。";
                return false;
            }

            if (!resource.HasCapability(SpeechCapability.RealtimeSpeechToText))
            {
                message = $"当前语音资源“{resource.Name}”不支持实时语音识别。";
                return false;
            }

            message = string.Empty;
            return true;
        }

        public bool TryGetActiveMicrosoftSpeechSubscriptionForRealtime(out AzureSubscription? subscription, out string message)
        {
            subscription = null;
            if (!TryGetActiveRealtimeSpeechResource(out var resource, out message))
            {
                return false;
            }

            if (resource!.ConnectorType != SpeechConnectorType.MicrosoftSpeech)
            {
                message = $"已选择“{resource.Name}”，但当前实时翻译暂仅支持 Microsoft Speech 连接器。";
                return false;
            }

            if (!resource.TryCreateAzureSubscription(out subscription) || subscription == null || !subscription.IsValid())
            {
                message = $"已选择“{resource.Name}”，但 Microsoft Speech 连接信息不完整。";
                return false;
            }

            message = $"当前语音资源：{resource.GetDisplayName()}";
            return true;
        }

        public string GetEffectiveActiveSpeechResourceId()
            => GetActiveSpeechResource()?.Id ?? "";

        public void EnsureSpeechResourcesBackfilledFromLegacy()
        {
            if (SpeechResources.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(ActiveSpeechResourceId))
                {
                    ActiveSpeechResourceId = SpeechResources.FirstOrDefault(resource => resource.IsEnabled)?.Id
                                            ?? SpeechResources.FirstOrDefault()?.Id
                                            ?? "";
                }

                return;
            }

            SpeechResources = GetEffectiveSpeechResources();
            ActiveSpeechResourceId = GetEffectiveActiveSpeechResourceId();
        }

        /// <summary>
        /// 将 Endpoints 中的 AzureSpeech 类型同步回 SpeechResources，
        /// 保证依赖旧 SpeechResource 的业务（SpeechTranslationService 等）继续工作。
        /// </summary>
        public void SyncSpeechResourcesFromEndpoints()
        {
            if (!SpeechSubscriptionsMigratedToEndpoints)
                return;

            var speechEndpoints = Endpoints
                .Where(ep => ep.EndpointType == EndpointApiType.AzureSpeech)
                .ToList();

            if (speechEndpoints.Count == 0 && SpeechResources.All(r => r.ConnectorType == SpeechConnectorType.MicrosoftSpeech))
            {
                SpeechResources.Clear();
                return;
            }

            // 重建 MicrosoftSpeech 类型的 SpeechResources
            var rebuilt = new List<SpeechResource>();
            foreach (var ep in speechEndpoints)
            {
                var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                    ? ep.SpeechRegion
                    : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint ?? "") ?? "";

                rebuilt.Add(new SpeechResource
                {
                    Id = ep.Id,
                    Name = ep.Name,
                    Vendor = SpeechVendorType.Microsoft,
                    ConnectorType = SpeechConnectorType.MicrosoftSpeech,
                    IsEnabled = ep.IsEnabled,
                    Capabilities = ep.SpeechCapabilities != SpeechCapability.None
                        ? ep.SpeechCapabilities
                        : SpeechCapability.RealtimeSpeechToText | SpeechCapability.BatchSpeechToText,
                    SubscriptionName = ep.Name,
                    SubscriptionKey = ep.SpeechSubscriptionKey ?? "",
                    ServiceRegion = region,
                    Endpoint = ep.SpeechEndpoint ?? "",
                });
            }

            // 保留非 MicrosoftSpeech 类型的资源（如 AiSpeech）
            var nonMicrosoftResources = SpeechResources
                .Where(r => r.ConnectorType != SpeechConnectorType.MicrosoftSpeech)
                .ToList();
            rebuilt.AddRange(nonMicrosoftResources);

            SpeechResources = rebuilt;

            // 同步 Subscriptions 以兼容旧逻辑
            Subscriptions = speechEndpoints.Select(ep =>
            {
                var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                    ? ep.SpeechRegion
                    : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint) ?? "";
                return new AzureSubscription
                {
                    Name = ep.Name,
                    SubscriptionKey = ep.SpeechSubscriptionKey ?? "",
                    ServiceRegion = region,
                    Endpoint = ep.SpeechEndpoint ?? ""
                };
            }).ToList();
        }

        /// <summary>
        /// 将旧的 Subscriptions / SpeechResources 一次性迁移到 Endpoints（AzureSpeech 类型）。
        /// 迁移完成后设置标记，后续启动不再重复执行。
        /// </summary>
        public void EnsureSpeechSubscriptionsMigratedToEndpoints()
        {
            if (SpeechSubscriptionsMigratedToEndpoints)
                return;

            // 先保证 SpeechResources 已从旧 Subscriptions 回填
            EnsureSpeechResourcesBackfilledFromLegacy();

            if (SpeechResources.Count == 0 && Subscriptions.Count == 0)
            {
                SpeechSubscriptionsMigratedToEndpoints = true;
                return;
            }

            // 已经有 AzureSpeech 类型终结点时跳过（避免重复）
            if (Endpoints.Any(ep => ep.EndpointType == EndpointApiType.AzureSpeech))
            {
                SpeechSubscriptionsMigratedToEndpoints = true;
                return;
            }

            string? activeEndpointId = null;

            foreach (var resource in SpeechResources.Where(r => r.ConnectorType == SpeechConnectorType.MicrosoftSpeech))
            {
                var region = !string.IsNullOrWhiteSpace(resource.ServiceRegion)
                    ? resource.ServiceRegion
                    : AzureSubscription.ParseRegionFromEndpoint(resource.Endpoint ?? "") ?? "";

                var ep = new AiEndpoint
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = string.IsNullOrWhiteSpace(resource.Name) ? $"Azure Speech ({region})" : resource.Name,
                    IsEnabled = resource.IsEnabled,
                    EndpointType = EndpointApiType.AzureSpeech,
                    ProfileId = "builtin.microsoft.azure-speech",
                    SpeechSubscriptionKey = resource.SubscriptionKey ?? "",
                    SpeechRegion = region,
                    SpeechEndpoint = resource.Endpoint ?? "",
                    SpeechCapabilities = resource.Capabilities != SpeechCapability.None
                        ? resource.Capabilities
                        : SpeechCapability.RealtimeSpeechToText | SpeechCapability.BatchSpeechToText,
                };

                Endpoints.Add(ep);

                if (string.Equals(resource.Id, ActiveSpeechResourceId, StringComparison.OrdinalIgnoreCase))
                    activeEndpointId = ep.Id;
            }

            if (activeEndpointId != null)
                ActiveSpeechResourceId = activeEndpointId;

            SpeechSubscriptionsMigratedToEndpoints = true;
        }
    }
}
