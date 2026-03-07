using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services;

public sealed class EndpointTemplateService : IEndpointTemplateService
{
    private readonly IEndpointProfileCatalogService _profileCatalogService;

    public EndpointTemplateService(IEndpointProfileCatalogService profileCatalogService)
    {
        _profileCatalogService = profileCatalogService;
    }

    public IReadOnlyList<EndpointTemplateDefinition> GetTemplates()
        => _profileCatalogService.GetProfiles()
            .Select(MapTemplate)
            .ToList();

    public EndpointTemplateDefinition GetTemplate(EndpointApiType type)
        => MapTemplate(_profileCatalogService.GetProfile(type));

    public EndpointTemplateDefinition GetTemplate(AiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var profile = !string.IsNullOrWhiteSpace(endpoint.ProfileId)
            ? _profileCatalogService.FindProfile(endpoint.ProfileId)
            : null;

        return MapTemplate(profile ?? _profileCatalogService.GetProfile(endpoint.EndpointType));
    }

    public void ApplyTemplate(AiEndpoint endpoint, EndpointApiType type)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var profile = _profileCatalogService.GetProfile(type);
        var defaults = profile.Defaults;

        endpoint.ProfileId = profile.Id;
        endpoint.EndpointType = type;

        endpoint.ProviderType = defaults.ProviderType;
        endpoint.AuthMode = defaults.AuthMode;
        endpoint.ApiKeyHeaderMode = defaults.ApiKeyHeaderMode;
        endpoint.TextApiProtocolMode = defaults.TextApiProtocolMode;
        endpoint.ImageApiRouteMode = defaults.ImageApiRouteMode;
        endpoint.ApiVersion = defaults.ApiVersion?.Trim() ?? string.Empty;

        if (defaults.ClearAzureIdentityFields)
        {
            endpoint.AzureTenantId = "";
            endpoint.AzureClientId = "";
        }
    }

    public string BuildBehaviorSummary(AiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var auth = endpoint.AuthMode == AzureAuthMode.AAD
            ? "Microsoft Entra ID (AAD)"
            : endpoint.ApiKeyHeaderMode switch
            {
                ApiKeyHeaderMode.ApiKeyHeader => "api-key Header",
                ApiKeyHeaderMode.Bearer => "Authorization: Bearer",
                _ => endpoint.IsAzureEndpoint ? "api-key Header（自动）" : "Authorization: Bearer（自动）"
            };

        var text = GetEffectiveTextProtocol(endpoint) switch
        {
            TextApiProtocolMode.ChatCompletionsV1 => "/v1/chat/completions",
            TextApiProtocolMode.ChatCompletionsRaw => "/chat/completions",
            TextApiProtocolMode.Responses => "/responses",
            _ => endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "/responses（自动）"
                : endpoint.IsAzureEndpoint
                    ? "Azure deployments（自动）"
                    : "/v1/chat/completions（自动）"
        };

        var image = endpoint.ImageApiRouteMode switch
        {
            ImageApiRouteMode.V1Images => "/v1/images",
            ImageApiRouteMode.ImagesRaw => "/images",
            _ => endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "/v1/images → /images（自动兜底）"
                : endpoint.IsAzureEndpoint
                    ? "Azure 官方图片路线（自动）"
                    : "/v1/images（自动）"
        };

        var version = string.IsNullOrWhiteSpace(endpoint.ApiVersion)
            ? "未显式填写"
            : endpoint.ApiVersion.Trim();

        return $"当前模板策略：文本 {text}；图片 {image}；认证 {auth}；API 版本 {version}。";
    }

    public string BuildInspectionDetails(AiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var template = GetTemplate(endpoint);
        var auth = DescribeAuth(endpoint);
        var textModels = DescribeModels(endpoint, ModelCapability.Text);
        var imageModels = DescribeModels(endpoint, ModelCapability.Image);
        var videoModels = DescribeModels(endpoint, ModelCapability.Video);
        var textRoute = DescribeTextRoute(endpoint);
        var imageRoute = DescribeImageRoute(endpoint);
        var videoRoute = DescribeVideoRoute(endpoint);
        var textPreview = BuildTextPreviewUrl(endpoint);
        var imagePreview = BuildImagePreviewUrls(endpoint);
        var videoPreview = BuildVideoPreviewUrls(endpoint);
        var version = string.IsNullOrWhiteSpace(endpoint.ApiVersion)
            ? (endpoint.IsAzureEndpoint ? "2024-02-01（Azure deployments 默认回退）" : "未显式填写")
            : endpoint.ApiVersion.Trim();

        return $"""
终结点名称
{endpoint.Name}

终结点 API 类型
{template.DisplayName}

模板说明
{template.Subtitle}

原始地址
{(string.IsNullOrWhiteSpace(endpoint.BaseUrl) ? "未填写" : endpoint.BaseUrl.Trim())}

当前认证
{auth}

【文字能力】
模型
{textModels}

接口路线
{textRoute}

请求 URL 示例
{textPreview}

【图片能力】
模型
{imageModels}

接口路线
{imageRoute}

请求 URL 示例
{imagePreview}

【视频能力】
模型
{videoModels}

接口路线
{videoRoute}

请求 URL 示例
{videoPreview}

API 版本
{version}

说明
- 终结点地址按你填写的原样保存。
- 这里展示的是当前模板 + 当前终结点字段推导出的文字 / 图片 / 视频请求示例，不会修改你的配置。
- 若后续切换终结点类型，这里的文本 / 图片路线与认证方式也会随模板一起变化。
""";
    }

    private static string DescribeAuth(AiEndpoint endpoint)
    {
        if (endpoint.AuthMode == AzureAuthMode.AAD)
            return "Authorization: Bearer（Microsoft Entra ID / AAD）";

        return endpoint.ApiKeyHeaderMode switch
        {
            ApiKeyHeaderMode.ApiKeyHeader => "api-key Header",
            ApiKeyHeaderMode.Bearer => "Authorization: Bearer",
            _ => endpoint.IsAzureEndpoint || endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "api-key Header（自动）"
                : "Authorization: Bearer（自动）"
        };
    }

    private static string DescribeTextRoute(AiEndpoint endpoint)
    {
        if (endpoint.IsAzureEndpoint)
            return "Azure deployments / chat/completions";

        return GetEffectiveTextProtocol(endpoint) switch
        {
            TextApiProtocolMode.Responses => "/responses",
            TextApiProtocolMode.ChatCompletionsRaw => "/chat/completions",
            _ => endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "/v1/chat/completions（资料包候选）"
                : "/v1/chat/completions"
        };
    }

    private static string DescribeImageRoute(AiEndpoint endpoint)
    {
        var urls = EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates(
            endpoint.BaseUrl ?? string.Empty,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ImageApiRouteMode,
            endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.DeploymentName,
            endpoint.ApiVersion);

        if (urls.Count == 0)
            return endpoint.ImageApiRouteMode == ImageApiRouteMode.ImagesRaw
                ? "/images/generations"
                : "/v1/images/generations";

        return string.Join(Environment.NewLine, urls.Select(url => ExtractImageRouteLabel(url)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string DescribeVideoRoute(AiEndpoint endpoint)
    {
        if (endpoint.IsAzureEndpoint)
        {
            return "/openai/v1/videos（sora2）\n/openai/v1/video/generations/jobs?api-version=preview（sora1 兼容）";
        }

        return endpoint.EndpointType == EndpointApiType.ApiManagementGateway
            ? "/v1/videos（按资料包候选顺序尝试，必要时自动补 api-version）"
            : "/v1/videos";
    }

    private static string BuildTextPreviewUrl(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "未填写地址，暂无法生成示例 URL。";

        var deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Text))?.DeploymentName;
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Text))?.ModelId;

        var urls = EndpointProfileUrlBuilder.BuildTextUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.TextApiProtocolMode,
            endpoint.IsAzureEndpoint,
            string.IsNullOrWhiteSpace(deployment) ? "{deployment}" : deployment.Trim(),
            endpoint.ApiVersion);

        return urls.Count == 0
            ? "未填写地址，暂无法生成示例 URL。"
            : string.Join(Environment.NewLine, urls.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildImagePreviewUrls(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "未填写地址，暂无法生成示例 URL。";

        var deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.DeploymentName;
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.ModelId;

        var urls = EndpointProfileUrlBuilder.BuildImageGenerateUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ImageApiRouteMode,
            deployment,
            endpoint.ApiVersion);

        return urls.Count == 0
            ? "未填写地址，暂无法生成示例 URL。"
            : string.Join(Environment.NewLine, urls.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildVideoPreviewUrls(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "未填写地址，暂无法生成示例 URL。";

        var urls = new List<string>();

        if (endpoint.IsAzureEndpoint)
        {
            urls.Add($"创建（sora2）: {baseUrl}/openai/v1/videos");
            urls.Add($"轮询（sora2）: {baseUrl}/openai/v1/videos/{{video_id}}");
            urls.Add($"下载（sora2）: {baseUrl}/openai/v1/videos/{{video_id}}/content");
            urls.Add($"创建（sora1 兼容）: {baseUrl}/openai/v1/video/generations/jobs?api-version=preview");
            urls.Add($"轮询（sora1 兼容）: {baseUrl}/openai/v1/video/generations/jobs/{{job_id}}?api-version=preview");
            urls.Add($"下载（sora1 兼容）: {baseUrl}/openai/v1/video/generations/jobs/{{job_id}}/content?api-version=preview");
            return string.Join(Environment.NewLine, urls);
        }

        var createUrls = EndpointProfileUrlBuilder.BuildVideoCreateUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            VideoApiMode.Videos);
        var pollUrls = EndpointProfileUrlBuilder.BuildVideoPollUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);
        var downloadUrls = EndpointProfileUrlBuilder.BuildVideoDownloadUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);
        var contentVideoUrls = EndpointProfileUrlBuilder.BuildVideoDownloadVideoContentUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);

        foreach (var createUrl in createUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"创建: {createUrl}");
        foreach (var pollUrl in pollUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"轮询: {pollUrl}");
        foreach (var downloadUrl in downloadUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"下载: {downloadUrl}");
        foreach (var contentVideoUrl in contentVideoUrls.Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"下载(video): {contentVideoUrl}");
        return string.Join(Environment.NewLine, urls);
    }

    private static string DescribeModels(AiEndpoint endpoint, ModelCapability capability)
    {
        var models = endpoint.Models
            .Where(model => model.Capabilities.HasFlag(capability))
            .Select(model => string.IsNullOrWhiteSpace(model.ModelId) ? "（未命名模型）" : model.ModelId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return models.Count == 0
            ? "未配置该类型模型"
            : string.Join("、", models);
    }

    private static TextApiProtocolMode GetEffectiveTextProtocol(AiEndpoint endpoint)
        => EndpointProfileUrlBuilder.GetEffectiveTextProtocol(
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.TextApiProtocolMode,
            endpoint.IsAzureEndpoint);

    private static ImageApiRouteMode GetEffectiveImageRouteMode(AiEndpoint endpoint)
    {
        if (endpoint.ImageApiRouteMode != ImageApiRouteMode.Auto)
            return endpoint.ImageApiRouteMode;

        return ImageApiRouteMode.V1Images;
    }

    private static string ExtractImageRouteLabel(string url)
    {
        var normalized = url.Replace("\\", "/");

        if (normalized.IndexOf("/deployments/", StringComparison.OrdinalIgnoreCase) >= 0)
            return "/openai/deployments/{deployment}/images/generations";
        if (normalized.IndexOf("/openai/v1/images/", StringComparison.OrdinalIgnoreCase) >= 0)
            return "/openai/v1/images/generations";
        if (normalized.IndexOf("/v1/images/", StringComparison.OrdinalIgnoreCase) >= 0)
            return "/v1/images/generations";
        if (normalized.IndexOf("/images/", StringComparison.OrdinalIgnoreCase) >= 0)
            return "/images/generations";

        return url;
    }

    private static string BuildImageUrl(string baseUrl, ImageApiRouteMode mode)
        => mode == ImageApiRouteMode.ImagesRaw
            ? AppendPath(baseUrl, "images/generations")
            : AppendPath(baseUrl, "v1/images/generations");

    private static string AppendPath(string baseUrl, string path)
    {
        var normalizedPath = path.TrimStart('/');
        return baseUrl.EndsWith($"/{normalizedPath}", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl}/{normalizedPath}";
    }

    private static string AppendOptionalApiVersion(string url, string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return url;

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}api-version={Uri.EscapeDataString(apiVersion.Trim())}";
    }

    private static EndpointTemplateDefinition MapTemplate(EndpointProfileDefinition profile)
        => new()
        {
            ProfileId = profile.Id,
            Type = profile.EndpointType,
            DisplayName = profile.DisplayName,
            Subtitle = profile.Subtitle,
            Glyph = profile.Glyph,
            Summary = profile.Summary,
            DefaultNamePrefix = profile.DefaultNamePrefix,
            IconAssetPath = profile.IconAssetPath,
            SupportsAad = profile.Defaults.SupportsAad
        };
}