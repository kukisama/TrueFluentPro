using System;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IRealtimeTranslationServiceFactory
    {
        bool TryResolveConnectorFamily(AzureSpeechConfig config, out RealtimeConnectorFamily connectorFamily, out string errorMessage);

        bool TryCreate(
            AzureSpeechConfig config,
            Action<string>? auditLog,
            out IRealtimeTranslationService? service,
            out string errorMessage);
    }

    public sealed class RealtimeTranslationServiceFactory : IRealtimeTranslationServiceFactory
    {
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IRealtimeConnectionSpecResolver _realtimeConnectionSpecResolver;

        public RealtimeTranslationServiceFactory(
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IRealtimeConnectionSpecResolver realtimeConnectionSpecResolver)
        {
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _realtimeConnectionSpecResolver = realtimeConnectionSpecResolver;
        }

        public bool TryResolveConnectorFamily(AzureSpeechConfig config, out RealtimeConnectorFamily connectorFamily, out string errorMessage)
        {
            connectorFamily = RealtimeConnectorFamily.Custom;
            errorMessage = string.Empty;

            if (!_speechResourceRuntimeResolver.TryResolveActive(
                    config,
                    SpeechCapability.RealtimeSpeechToText,
                    out var runtime,
                    out errorMessage) || runtime == null)
            {
                return false;
            }

            if (runtime.IsMicrosoftSpeech)
            {
                connectorFamily = RealtimeConnectorFamily.MicrosoftSpeechSdk;
                return true;
            }

            if (runtime.AiRuntime == null)
            {
                errorMessage = $"语音资源“{runtime.Resource.Name}”缺少可用的 Realtime AI 运行时。";
                return false;
            }

            if (!_realtimeConnectionSpecResolver.TryResolve(runtime.AiRuntime, out var spec, out errorMessage) || spec == null)
            {
                return false;
            }

            connectorFamily = spec.ConnectorFamily;
            return true;
        }

        public bool TryCreate(
            AzureSpeechConfig config,
            Action<string>? auditLog,
            out IRealtimeTranslationService? service,
            out string errorMessage)
        {
            service = null;

            if (!_speechResourceRuntimeResolver.TryResolveActive(
                    config,
                    SpeechCapability.RealtimeSpeechToText,
                    out var runtime,
                    out errorMessage) || runtime == null)
            {
                return false;
            }

            if (runtime.IsMicrosoftSpeech)
            {
                service = new SpeechTranslationService(config, auditLog);
                return true;
            }

            if (runtime.AiRuntime == null)
            {
                errorMessage = $"语音资源“{runtime.Resource.Name}”缺少可用的 Realtime AI 运行时。";
                return false;
            }

            if (!_realtimeConnectionSpecResolver.TryResolve(runtime.AiRuntime, out var spec, out errorMessage) || spec == null)
            {
                return false;
            }

            if (spec.ConnectorFamily != RealtimeConnectorFamily.OpenAiRealtimeWebSocket)
            {
                errorMessage = $"暂未实现“{spec.ConnectorFamily}”的实时翻译执行器。";
                return false;
            }

            service = new OpenAiRealtimeTranslationService(
                config,
                _modelRuntimeResolver,
                _speechResourceRuntimeResolver,
                _realtimeConnectionSpecResolver,
                auditLog);
            return true;
        }
    }
}