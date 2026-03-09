using System;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public enum RealtimeConnectorFamily
    {
        MicrosoftSpeechSdk,
        OpenAiRealtimeWebSocket,
        Custom
    }

    public enum RealtimeEndpointRouteKind
    {
        None,
        OpenAiGa,
        AzureOpenAiGa,
        AzureOpenAiPreview
    }

    public enum RealtimeAuthTransportKind
    {
        AuthorizationBearer,
        ApiKeyHeader,
        ApiKeyQuery
    }

    public enum RealtimeSessionMode
    {
        Conversation,
        Transcription
    }

    public sealed class RealtimeConnectionSpec
    {
        public RealtimeConnectorFamily ConnectorFamily { get; init; }
        public RealtimeEndpointRouteKind RouteKind { get; init; }
        public Uri WebSocketUri { get; init; } = null!;
        public bool IsPreview { get; init; }
        public string ModelName { get; init; } = string.Empty;
        public string DeploymentName { get; init; } = string.Empty;
        public string ApiVersion { get; init; } = string.Empty;
        public RealtimeAuthTransportKind AuthTransportKind { get; init; }
        public RealtimeSessionMode SessionMode { get; init; } = RealtimeSessionMode.Conversation;
        public bool UsesOpenAiRealtimeProtocol => ConnectorFamily == RealtimeConnectorFamily.OpenAiRealtimeWebSocket;
    }

    public interface IRealtimeConnectionSpecResolver
    {
        bool TryResolve(ModelRuntimeResolution runtime, out RealtimeConnectionSpec? spec, out string errorMessage);
    }

    /// <summary>
    /// 按官方文档解析 Realtime 连接入口。
    /// 
    /// 当前仅明确支持：
    /// 1. Microsoft Speech SDK（单独通道，不在这里解析 URL）
    /// 2. OpenAI 官方 Realtime WebSocket
    /// 3. Azure OpenAI / Foundry 官方 Realtime WebSocket
    /// 
    /// 不默认假设任意 OpenAI Compatible / APIM 都兼容官方 Realtime 协议。
    /// </summary>
    public sealed class RealtimeConnectionSpecResolver : IRealtimeConnectionSpecResolver
    {
        private const string AzurePreviewRealtimeApiVersion = "2025-04-01-preview";

        public bool TryResolve(ModelRuntimeResolution runtime, out RealtimeConnectionSpec? spec, out string errorMessage)
        {
            spec = null;
            errorMessage = string.Empty;

            if (runtime == null)
            {
                errorMessage = "运行时为空，无法解析 Realtime 连接。";
                return false;
            }

            var modelName = string.IsNullOrWhiteSpace(runtime.ModelId)
                ? runtime.EffectiveDeploymentName
                : runtime.ModelId;
            var deploymentName = runtime.EffectiveDeploymentName;
            var sessionMode = ResolveSessionMode(modelName, deploymentName);

            if (!LooksLikeSupportedRealtimeInputModel(modelName) && !LooksLikeSupportedRealtimeInputModel(deploymentName))
            {
                errorMessage = $"模型“{runtime.DisplayName}”不像官方 Realtime/Realtime Transcription 模型，当前不会按官方 Realtime 通道连接。";
                return false;
            }

            switch (runtime.EndpointType)
            {
                case EndpointApiType.AzureOpenAi:
                    return TryResolveAzureOpenAi(runtime, modelName, deploymentName, sessionMode, out spec, out errorMessage);

                case EndpointApiType.OpenAiCompatible:
                    return TryResolveOpenAi(runtime, modelName, deploymentName, sessionMode, out spec, out errorMessage);

                case EndpointApiType.ApiManagementGateway:
                    errorMessage = "APIM 网关暂不自动视为官方 Realtime 入口；后续应单独声明其 Realtime 路由与鉴权策略。";
                    return false;

                default:
                    errorMessage = $"暂不支持终结点类型“{runtime.EndpointType}”的 Realtime 连接解析。";
                    return false;
            }
        }

        private static bool TryResolveAzureOpenAi(
            ModelRuntimeResolution runtime,
            string modelName,
            string deploymentName,
            RealtimeSessionMode sessionMode,
            out RealtimeConnectionSpec? spec,
            out string errorMessage)
        {
            spec = null;
            errorMessage = string.Empty;

            if (!TryBuildBaseWebSocketUri(runtime.ApiEndpoint, out var baseUri, out errorMessage))
            {
                return false;
            }

            var isPreview = IsPreviewRealtimeModel(modelName)
                            || IsPreviewRealtimeModel(deploymentName);

            var builder = new UriBuilder(baseUri);
            if (isPreview)
            {
                var apiVersion = string.IsNullOrWhiteSpace(runtime.ApiVersion)
                    ? AzurePreviewRealtimeApiVersion
                    : runtime.ApiVersion.Trim();
                builder.Path = "/openai/realtime";
                builder.Query = $"api-version={Uri.EscapeDataString(apiVersion)}&deployment={Uri.EscapeDataString(deploymentName)}";

                spec = new RealtimeConnectionSpec
                {
                    ConnectorFamily = RealtimeConnectorFamily.OpenAiRealtimeWebSocket,
                    RouteKind = RealtimeEndpointRouteKind.AzureOpenAiPreview,
                    WebSocketUri = builder.Uri,
                    IsPreview = true,
                    ModelName = modelName,
                    DeploymentName = deploymentName,
                    ApiVersion = apiVersion,
                    SessionMode = sessionMode,
                    AuthTransportKind = runtime.AzureAuthMode == AzureAuthMode.AAD
                        ? RealtimeAuthTransportKind.AuthorizationBearer
                        : RealtimeAuthTransportKind.ApiKeyHeader
                };
                return true;
            }

            builder.Path = "/openai/v1/realtime";
            builder.Query = $"model={Uri.EscapeDataString(deploymentName)}";

            spec = new RealtimeConnectionSpec
            {
                ConnectorFamily = RealtimeConnectorFamily.OpenAiRealtimeWebSocket,
                RouteKind = RealtimeEndpointRouteKind.AzureOpenAiGa,
                WebSocketUri = builder.Uri,
                IsPreview = false,
                ModelName = modelName,
                DeploymentName = deploymentName,
                ApiVersion = string.Empty,
                SessionMode = sessionMode,
                AuthTransportKind = runtime.AzureAuthMode == AzureAuthMode.AAD
                    ? RealtimeAuthTransportKind.AuthorizationBearer
                    : RealtimeAuthTransportKind.ApiKeyHeader
            };
            return true;
        }

        private static bool TryResolveOpenAi(
            ModelRuntimeResolution runtime,
            string modelName,
            string deploymentName,
            RealtimeSessionMode sessionMode,
            out RealtimeConnectionSpec? spec,
            out string errorMessage)
        {
            spec = null;
            errorMessage = string.Empty;

            if (!TryBuildBaseWebSocketUri(runtime.ApiEndpoint, out var baseUri, out errorMessage))
            {
                return false;
            }

            if (!string.Equals(baseUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = $"终结点“{runtime.EndpointName}”属于 OpenAI Compatible，但主机“{baseUri.Host}”不是 OpenAI 官方地址；当前不默认套用官方 Realtime 协议。";
                return false;
            }

            if (IsPreviewRealtimeModel(modelName) || IsPreviewRealtimeModel(deploymentName))
            {
                errorMessage = $"OpenAI 官方预览 Realtime 模型“{runtime.DisplayName}”当前仍会走 preview 协议分支；本客户端现在只按官方 GA 协议实现 OpenAI 实时翻译。请先改用 gpt-realtime（或其它 GA realtime 模型），若要支持 preview 模型需后续单独补一套 preview 事件与 session 配置。";
                return false;
            }

            var builder = new UriBuilder(baseUri)
            {
                Path = "/v1/realtime",
                Query = $"model={Uri.EscapeDataString(modelName)}"
            };

            spec = new RealtimeConnectionSpec
            {
                ConnectorFamily = RealtimeConnectorFamily.OpenAiRealtimeWebSocket,
                RouteKind = RealtimeEndpointRouteKind.OpenAiGa,
                WebSocketUri = builder.Uri,
                IsPreview = false,
                ModelName = modelName,
                DeploymentName = deploymentName,
                ApiVersion = string.Empty,
                SessionMode = sessionMode,
                AuthTransportKind = RealtimeAuthTransportKind.AuthorizationBearer
            };
            return true;
        }

        private static bool TryBuildBaseWebSocketUri(string endpoint, out Uri uri, out string errorMessage)
        {
            uri = null!;
            errorMessage = string.Empty;

            var normalized = (endpoint ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                errorMessage = "终结点地址为空。";
                return false;
            }

            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://" + normalized;
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed))
            {
                errorMessage = $"终结点地址无效：{endpoint}";
                return false;
            }

            var builder = new UriBuilder(parsed)
            {
                Scheme = parsed.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    ? "ws"
                    : "wss",
                Port = parsed.IsDefaultPort ? -1 : parsed.Port,
                Path = string.Empty,
                Query = string.Empty
            };

            uri = builder.Uri;
            return true;
        }

        private static RealtimeSessionMode ResolveSessionMode(string? modelName, string? deploymentName)
            => LooksLikeRealtimeTranscriptionModel(modelName) || LooksLikeRealtimeTranscriptionModel(deploymentName)
                ? RealtimeSessionMode.Transcription
                : RealtimeSessionMode.Conversation;

        private static bool LooksLikeSupportedRealtimeInputModel(string? value)
            => LooksLikeRealtimeModel(value) || LooksLikeRealtimeTranscriptionModel(value);

        private static bool LooksLikeRealtimeModel(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && value.IndexOf("realtime", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool LooksLikeRealtimeTranscriptionModel(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && (value.IndexOf("transcribe", StringComparison.OrdinalIgnoreCase) >= 0
                   || string.Equals(value, "whisper-1", StringComparison.OrdinalIgnoreCase));

        private static bool IsPreviewRealtimeModel(string? value)
            => !string.IsNullOrWhiteSpace(value)
               && value.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0;

    }
}
