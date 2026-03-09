using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SpeechCapability
    {
        None = 0,
        RealtimeSpeechToText = 1,
        BatchSpeechToText = 2,
        TextToSpeech = 4
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SpeechVendorType
    {
        Microsoft,
        OpenAI,
        Tencent,
        Alibaba,
        Other
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SpeechConnectorType
    {
        MicrosoftSpeech,
        AiSpeech,
        CustomSpeech
    }

    public class SpeechResource
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public SpeechVendorType Vendor { get; set; } = SpeechVendorType.Microsoft;
        public SpeechConnectorType ConnectorType { get; set; } = SpeechConnectorType.MicrosoftSpeech;
        public bool IsEnabled { get; set; } = true;
        public SpeechCapability Capabilities { get; set; } = SpeechCapability.None;

        // 兼容微软 Speech 资源的连接信息。后续若扩展更多传统 speech 厂商，可再抽成独立详情模型。
        public string SubscriptionName { get; set; } = "";
        public string SubscriptionKey { get; set; } = "";
        public string ServiceRegion { get; set; } = "";
        public string Endpoint { get; set; } = "";

        // AI 语音资源只引用模型，不重复保存终结点连接信息。
        public ModelReference? RealtimeSpeechToTextModelRef { get; set; }
        public ModelReference? BatchSpeechToTextModelRef { get; set; }
        public ModelReference? TextToSpeechModelRef { get; set; }

        public static SpeechResource CreateMicrosoftResource(AzureSubscription subscription, int index)
        {
            return new SpeechResource
            {
                Id = BuildLegacyMicrosoftResourceId(index),
                Name = string.IsNullOrWhiteSpace(subscription.Name) ? $"Microsoft Speech {index + 1}" : subscription.Name.Trim(),
                Vendor = SpeechVendorType.Microsoft,
                ConnectorType = SpeechConnectorType.MicrosoftSpeech,
                IsEnabled = true,
                Capabilities = SpeechCapability.RealtimeSpeechToText | SpeechCapability.BatchSpeechToText,
                SubscriptionName = subscription.Name?.Trim() ?? "",
                SubscriptionKey = subscription.SubscriptionKey?.Trim() ?? "",
                ServiceRegion = subscription.GetEffectiveRegion(),
                Endpoint = subscription.GetEffectiveEndpoint()
            };
        }

        public static SpeechResource? CreateLegacyAiResource(
            ModelReference? realtimeModelRef,
            ModelReference? batchModelRef,
            ModelReference? ttsModelRef)
        {
            var capabilities = SpeechCapability.None;
            if (realtimeModelRef != null)
            {
                capabilities |= SpeechCapability.RealtimeSpeechToText;
            }

            if (batchModelRef != null)
            {
                capabilities |= SpeechCapability.BatchSpeechToText;
            }

            if (ttsModelRef != null)
            {
                capabilities |= SpeechCapability.TextToSpeech;
            }

            if (capabilities == SpeechCapability.None)
            {
                return null;
            }

            return new SpeechResource
            {
                Id = LegacyAiResourceId,
                Name = "默认 AI 语音资源",
                Vendor = SpeechVendorType.OpenAI,
                ConnectorType = SpeechConnectorType.AiSpeech,
                IsEnabled = true,
                Capabilities = capabilities,
                RealtimeSpeechToTextModelRef = CloneReference(realtimeModelRef),
                BatchSpeechToTextModelRef = CloneReference(batchModelRef),
                TextToSpeechModelRef = CloneReference(ttsModelRef)
            };
        }

        public static string BuildLegacyMicrosoftResourceId(int index) => $"legacy-microsoft-{index}";

        public const string LegacyAiResourceId = "legacy-ai-speech-default";

        public bool HasCapability(SpeechCapability capability)
            => (Capabilities & capability) == capability;

        public string GetDisplayName()
        {
            if (ConnectorType == SpeechConnectorType.MicrosoftSpeech)
            {
                var region = !string.IsNullOrWhiteSpace(ServiceRegion)
                    ? ServiceRegion
                    : AzureSubscription.ParseRegionFromEndpoint(Endpoint) ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(region))
                {
                    return $"{Name} ({region})";
                }
            }

            return Name;
        }

        public bool TryCreateAzureSubscription(out AzureSubscription? subscription)
        {
            subscription = null;
            if (ConnectorType != SpeechConnectorType.MicrosoftSpeech)
            {
                return false;
            }

            subscription = new AzureSubscription
            {
                Name = string.IsNullOrWhiteSpace(SubscriptionName) ? Name : SubscriptionName,
                SubscriptionKey = SubscriptionKey,
                ServiceRegion = ServiceRegion,
                Endpoint = Endpoint
            };

            return true;
        }

        public SpeechResource Clone()
        {
            return new SpeechResource
            {
                Id = Id,
                Name = Name,
                Vendor = Vendor,
                ConnectorType = ConnectorType,
                IsEnabled = IsEnabled,
                Capabilities = Capabilities,
                SubscriptionName = SubscriptionName,
                SubscriptionKey = SubscriptionKey,
                ServiceRegion = ServiceRegion,
                Endpoint = Endpoint,
                RealtimeSpeechToTextModelRef = CloneReference(RealtimeSpeechToTextModelRef),
                BatchSpeechToTextModelRef = CloneReference(BatchSpeechToTextModelRef),
                TextToSpeechModelRef = CloneReference(TextToSpeechModelRef)
            };
        }

        public IEnumerable<string> GetReferencedEndpointIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddReferenceId(ids, RealtimeSpeechToTextModelRef);
            AddReferenceId(ids, BatchSpeechToTextModelRef);
            AddReferenceId(ids, TextToSpeechModelRef);
            return ids;
        }

        private static void AddReferenceId(HashSet<string> ids, ModelReference? reference)
        {
            if (!string.IsNullOrWhiteSpace(reference?.EndpointId))
            {
                ids.Add(reference.EndpointId);
            }
        }

        private static ModelReference? CloneReference(ModelReference? reference)
        {
            if (reference == null)
            {
                return null;
            }

            return new ModelReference
            {
                EndpointId = reference.EndpointId,
                ModelId = reference.ModelId
            };
        }
    }
}
