using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public sealed class SpeechResourceRuntimeResolution
    {
        public SpeechResource Resource { get; init; } = null!;
        public SpeechCapability Capability { get; init; }
        public AzureSubscription? MicrosoftSubscription { get; init; }
        public ModelRuntimeResolution? AiRuntime { get; init; }

        public bool IsMicrosoftSpeech => Resource.ConnectorType == SpeechConnectorType.MicrosoftSpeech;
        public bool IsAiSpeech => Resource.ConnectorType == SpeechConnectorType.AiSpeech;
    }

    public interface ISpeechResourceRuntimeResolver
    {
        bool TryResolveActive(
            AzureSpeechConfig config,
            SpeechCapability capability,
            out SpeechResourceRuntimeResolution? runtime,
            out string errorMessage);
    }

    public sealed class SpeechResourceRuntimeResolver : ISpeechResourceRuntimeResolver
    {
        private readonly IModelRuntimeResolver _modelRuntimeResolver;

        public SpeechResourceRuntimeResolver(IModelRuntimeResolver modelRuntimeResolver)
        {
            _modelRuntimeResolver = modelRuntimeResolver;
        }

        public bool TryResolveActive(
            AzureSpeechConfig config,
            SpeechCapability capability,
            out SpeechResourceRuntimeResolution? runtime,
            out string errorMessage)
        {
            runtime = null;
            errorMessage = string.Empty;

            if (config == null)
            {
                errorMessage = "配置为空，无法解析语音资源。";
                return false;
            }

            var resource = config.GetActiveSpeechResource();
            if (resource == null)
            {
                errorMessage = "未选择语音资源。";
                return false;
            }

            if (!resource.IsEnabled)
            {
                errorMessage = $"语音资源“{resource.Name}”已禁用。";
                return false;
            }

            if (!resource.HasCapability(capability))
            {
                errorMessage = $"语音资源“{resource.Name}”不支持{GetCapabilityName(capability)}。";
                return false;
            }

            switch (resource.ConnectorType)
            {
                case SpeechConnectorType.MicrosoftSpeech:
                    if (!resource.TryCreateAzureSubscription(out var subscription)
                        || subscription == null
                        || !subscription.IsValid())
                    {
                        errorMessage = $"语音资源“{resource.Name}”的 Microsoft Speech 连接信息不完整。";
                        return false;
                    }

                    runtime = new SpeechResourceRuntimeResolution
                    {
                        Resource = resource,
                        Capability = capability,
                        MicrosoftSubscription = subscription
                    };
                    return true;

                case SpeechConnectorType.AiSpeech:
                    var reference = capability switch
                    {
                        SpeechCapability.RealtimeSpeechToText => resource.RealtimeSpeechToTextModelRef,
                        SpeechCapability.BatchSpeechToText => resource.BatchSpeechToTextModelRef,
                        SpeechCapability.TextToSpeech => resource.TextToSpeechModelRef,
                        _ => null
                    };

                    var modelCapability = capability switch
                    {
                        SpeechCapability.TextToSpeech => ModelCapability.TextToSpeech,
                        _ => ModelCapability.SpeechToText
                    };

                    if (!_modelRuntimeResolver.TryResolve(config, reference, modelCapability, out var aiRuntime, out errorMessage)
                        || aiRuntime == null)
                    {
                        errorMessage = string.IsNullOrWhiteSpace(errorMessage)
                            ? $"语音资源“{resource.Name}”缺少可用的 AI 运行时。"
                            : errorMessage;
                        return false;
                    }

                    runtime = new SpeechResourceRuntimeResolution
                    {
                        Resource = resource,
                        Capability = capability,
                        AiRuntime = aiRuntime
                    };
                    return true;

                default:
                    errorMessage = $"当前暂不支持语音资源“{resource.Name}”的连接器类型：{resource.ConnectorType}。";
                    return false;
            }
        }

        private static string GetCapabilityName(SpeechCapability capability)
            => capability switch
            {
                SpeechCapability.RealtimeSpeechToText => "实时语音转文字",
                SpeechCapability.BatchSpeechToText => "批量语音转文字",
                SpeechCapability.TextToSpeech => "文字转语音",
                _ => capability.ToString()
            };
    }
}