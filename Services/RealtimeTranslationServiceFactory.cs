using System;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// 对指定的讯飞 / 百度实时语音资源做握手探活（不开麦克风、不发送音频），
        /// 用于配置页与批量测试的连通性验证。
        /// </summary>
        Task<RealtimeProbeResult> ProbeThirdPartyRealtimeAsync(
            AzureSpeechConfig config,
            SpeechResource resource,
            CancellationToken cancellationToken);

        /// <summary>
        /// 对指定的讯飞 / 百度实时语音资源做翻译链路探活：翻译一小段示例文本，
        /// 验证机器翻译凭据是否可用。
        /// </summary>
        Task<RealtimeProbeResult> ProbeThirdPartyTranslationAsync(
            AzureSpeechConfig config,
            SpeechResource resource,
            CancellationToken cancellationToken);
    }

    public sealed class RealtimeTranslationServiceFactory : IRealtimeTranslationServiceFactory
    {
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IRealtimeConnectionSpecResolver _realtimeConnectionSpecResolver;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;

        public RealtimeTranslationServiceFactory(
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IRealtimeConnectionSpecResolver realtimeConnectionSpecResolver,
            IAzureTokenProviderStore azureTokenProviderStore)
        {
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _realtimeConnectionSpecResolver = realtimeConnectionSpecResolver;
            _azureTokenProviderStore = azureTokenProviderStore;
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

            if (runtime.IsXunfeiRtasr)
            {
                connectorFamily = RealtimeConnectorFamily.XunfeiRtasr;
                return true;
            }

            if (runtime.IsBaiduRealtimeAsr)
            {
                connectorFamily = RealtimeConnectorFamily.BaiduRealtimeAsr;
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
                service = new SpeechTranslationService(config, _azureTokenProviderStore, auditLog);
                return true;
            }

            if (runtime.IsXunfeiRtasr)
            {
                service = new XunfeiRealtimeTranslationService(config, _speechResourceRuntimeResolver, auditLog);
                return true;
            }

            if (runtime.IsBaiduRealtimeAsr)
            {
                service = new BaiduRealtimeTranslationService(config, _speechResourceRuntimeResolver, auditLog);
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
                _azureTokenProviderStore,
                auditLog);
            return true;
        }

        public async Task<RealtimeProbeResult> ProbeThirdPartyRealtimeAsync(
            AzureSpeechConfig config,
            SpeechResource resource,
            CancellationToken cancellationToken)
        {
            CascadedRealtimeTranslationServiceBase? probe = resource.ConnectorType switch
            {
                SpeechConnectorType.XunfeiRtasr => new XunfeiRealtimeTranslationService(config, _speechResourceRuntimeResolver, null),
                SpeechConnectorType.BaiduRealtimeAsr => new BaiduRealtimeTranslationService(config, _speechResourceRuntimeResolver, null),
                _ => null
            };

            if (probe == null)
            {
                return new RealtimeProbeResult(false, "该资源不是讯飞 / 百度实时语音类型，无法探活。", string.Empty, null);
            }

            return await probe.ProbeConnectionAsync(resource, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RealtimeProbeResult> ProbeThirdPartyTranslationAsync(
            AzureSpeechConfig config,
            SpeechResource resource,
            CancellationToken cancellationToken)
        {
            CascadedRealtimeTranslationServiceBase? probe = resource.ConnectorType switch
            {
                SpeechConnectorType.XunfeiRtasr => new XunfeiRealtimeTranslationService(config, _speechResourceRuntimeResolver, null),
                SpeechConnectorType.BaiduRealtimeAsr => new BaiduRealtimeTranslationService(config, _speechResourceRuntimeResolver, null),
                _ => null
            };

            if (probe == null)
            {
                return new RealtimeProbeResult(false, "该资源不是讯飞 / 百度实时语音类型，无法探活翻译。", string.Empty, null);
            }

            return await probe.ProbeTranslationAsync(resource, cancellationToken).ConfigureAwait(false);
        }
    }
}