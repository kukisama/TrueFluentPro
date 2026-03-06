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

        public string SourceLanguage { get; set; } = "auto";
        public string TargetLanguage { get; set; } = "zh-CN";

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

        public AudioSourceMode AudioSourceMode { get; set; } = AudioSourceMode.DefaultMic;
        public string SelectedAudioDeviceId { get; set; } = "";
        public string SelectedOutputDeviceId { get; set; } = "";
        public RecordingMode RecordingMode { get; set; } = RecordingMode.LoopbackWithMic;
        public bool UseInputForRecognition { get; set; } = true;
        public bool UseOutputForRecognition { get; set; } = false;

        public int ChunkDurationMs { get; set; } = 200;

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
        public bool UseSpeechSubtitleForReview { get; set; } = true;
        public BatchLogLevel BatchLogLevel { get; set; } = BatchLogLevel.Off;
        public bool BatchForceRegeneration { get; set; } = false;
        public bool ContextMenuForceRegeneration { get; set; } = true;
        public bool EnableBatchSubtitleSentenceSplit { get; set; } = true;
        public bool BatchSubtitleSplitOnComma { get; set; } = false;
        public int BatchSubtitleMaxChars { get; set; } = 24;
        public double BatchSubtitleMaxDurationSeconds { get; set; } = 6;
        public int BatchSubtitlePauseSplitMs { get; set; } = 500;

        public AiConfig? AiConfig { get; set; }

        public bool IsAutoUpdateEnabled { get; set; } = true;

        public MediaGenConfig MediaGenConfig { get; set; } = new();

        /// <summary>统一 AI 终结点注册表</summary>
        public List<AiEndpoint> Endpoints { get; set; } = new();

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
            var activeSubscription = GetActiveSubscription();
            return activeSubscription?.IsValid() == true &&
                   !string.IsNullOrEmpty(SourceLanguage) &&
                   !string.IsNullOrEmpty(TargetLanguage);
        }
    }
}
