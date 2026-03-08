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
            endpoint.AzureTenantId = string.Empty;
            endpoint.AzureClientId = string.Empty;
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

        var textUrls = BuildTextPreviewUrlList(endpoint);
        var imageUrls = BuildImagePreviewUrlList(endpoint);
        var videoUrls = BuildVideoPreviewUrlList(endpoint);

        var text = textUrls.Count > 0 ? "按资料包声明执行" : "资料包未声明";
        var image = imageUrls.Count > 0 ? "按资料包声明执行" : "资料包未声明";
        var video = videoUrls.Count > 0 ? "按资料包声明执行" : "资料包未声明";

        var version = string.IsNullOrWhiteSpace(endpoint.ApiVersion)
            ? "未显式填写"
            : endpoint.ApiVersion.Trim();

        return $"当前模板策略：文本 {text}；图片 {image}；视频 {video}；认证 {auth}；API 版本 {version}。";
    }

    public EndpointInspectionDetails BuildInspectionDetailsModel(AiEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var template = GetTemplate(endpoint);
        var summaryRows = new List<EndpointInspectionRow>
        {
            new("终结点 API 类型", template.DisplayName),
            new("模板说明", template.Subtitle),
            new("原始地址", string.IsNullOrWhiteSpace(endpoint.BaseUrl) ? "未填写" : endpoint.BaseUrl.Trim()),
            new("当前认证", DescribeAuth(endpoint))
        };

        var version = GetApiVersionDescription(endpoint);
        if (!string.IsNullOrWhiteSpace(version))
        {
            summaryRows.Add(new EndpointInspectionRow("API 版本", version));
        }

        var sections = new List<EndpointInspectionSection>();
        AppendCapabilitySectionModel(sections, endpoint, ModelCapability.Text, "文字能力", DescribeTextRoute(endpoint), BuildTextPreviewUrlList(endpoint));
        AppendCapabilitySectionModel(sections, endpoint, ModelCapability.Image, "图片能力", DescribeImageRoute(endpoint), BuildImagePreviewUrlList(endpoint));
        AppendCapabilitySectionModel(sections, endpoint, ModelCapability.Video, "视频能力", DescribeVideoRoute(endpoint), BuildVideoPreviewUrlList(endpoint));

        return new EndpointInspectionDetails(
            string.IsNullOrWhiteSpace(endpoint.Name) ? "当前终结点" : endpoint.Name,
            "这里展示当前终结点资料包已声明的认证方式、接口路线和 URL 示例，不会修改配置。",
            summaryRows,
            sections,
            string.Empty);
    }

    public string BuildInspectionDetails(AiEndpoint endpoint)
    {
        var details = BuildInspectionDetailsModel(endpoint);
        var lines = new List<string>
        {
            $"# {EscapeMarkdown(details.Title)}",
            string.Empty,
            EscapeMarkdown(details.Intro),
            string.Empty,
            "## 基本信息",
            string.Empty
        };

        foreach (var row in details.SummaryRows)
        {
            lines.Add($"{EscapeMarkdown(row.Label)}：{EscapeMarkdown(row.Value)}");
        }

        foreach (var section in details.Sections)
        {
            lines.Add(string.Empty);
            lines.Add($"## {EscapeMarkdown(section.Heading)}");
            lines.Add(string.Empty);

            foreach (var row in section.Rows)
            {
                lines.Add($"{EscapeMarkdown(row.Label)}：{EscapeMarkdown(row.Value)}");
            }

            if (section.UrlItems.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"URL 地址：共 {section.UrlItems.Count} 条资料包声明，带 * 表示首选命中地址");
                foreach (var urlItem in section.UrlItems)
                {
                    lines.Add($"{EscapeMarkdown(urlItem.Label)}：{EscapeMarkdown(urlItem.Url)}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(details.FooterNote))
        {
            lines.Add(string.Empty);
            lines.Add(EscapeMarkdown(details.FooterNote));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendCapabilitySectionModel(
        List<EndpointInspectionSection> sections,
        AiEndpoint endpoint,
        ModelCapability capability,
        string heading,
        string route,
        IReadOnlyList<string> previewUrls)
    {
        var models = GetCapabilityModels(endpoint, capability);
        if (models.Count == 0)
            return;

        var rows = new List<EndpointInspectionRow>
        {
            new("模型", string.Join("、", models)),
            new("接口路线", route)
        };

        var urlItems = previewUrls.Count == 0
            ? new List<EndpointInspectionUrlItem>
            {
                new("URL 示例", "资料包未声明；本工具不再自动补兜底路线，请厂商补齐资料包。")
            }
            : BuildUrlItems(previewUrls);

        sections.Add(new EndpointInspectionSection(heading, rows, urlItems));
    }

    private static void AppendCapabilitySection(
        List<string> lines,
        AiEndpoint endpoint,
        ModelCapability capability,
        string heading,
        string route,
        IReadOnlyList<string> previewUrls)
    {
        var models = GetCapabilityModels(endpoint, capability);
        if (models.Count == 0)
            return;

        lines.Add(string.Empty);
        lines.Add($"## {heading}");
        lines.Add(string.Empty);
        lines.Add($"模型：{EscapeMarkdown(string.Join("、", models))}");
        lines.Add($"接口路线：{EscapeMarkdown(route)}");

        if (previewUrls.Count == 0)
        {
            lines.Add("URL 示例：资料包未声明；本工具不再自动补兜底路线，请厂商补齐资料包。");
            return;
        }

        lines.Add(string.Empty);
        lines.Add($"URL 示例：共 {previewUrls.Count} 条资料包候选");
        for (var i = 0; i < previewUrls.Count; i++)
        {
            lines.Add($"候选 {i + 1}：{EscapeMarkdown(previewUrls[i])}");
        }
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
        var urls = BuildTextPreviewUrlList(endpoint);
        if (urls.Count == 0)
            return "资料包未声明文字请求路线";

        if (endpoint.IsAzureEndpoint)
            return "资料包声明的 Azure deployments 文字路线";

        return GetEffectiveTextProtocol(endpoint) switch
        {
            TextApiProtocolMode.Responses => "/responses（资料包）",
            TextApiProtocolMode.ChatCompletionsRaw => "/chat/completions（资料包）",
            TextApiProtocolMode.ChatCompletionsV1 => "/v1/chat/completions（资料包）",
            _ => "资料包已声明，但未标注首选文本协议"
        };
    }

    private static string DescribeImageRoute(AiEndpoint endpoint)
    {
        var urls = EndpointProfileUrlBuilder.BuildConfiguredImageGenerateUrlCandidates(
            endpoint.BaseUrl ?? string.Empty,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ImageApiRouteMode,
            endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.DeploymentName,
            endpoint.ApiVersion);

        if (urls.Count == 0)
            return "资料包未声明图片生成路线";

        return string.Join(Environment.NewLine, urls.Select(ExtractImageRouteLabel).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string DescribeVideoRoute(AiEndpoint endpoint)
    {
        var urls = BuildVideoPreviewUrlList(endpoint);
        return urls.Count == 0
            ? "资料包未声明视频路线"
            : "按资料包声明的视频创建 / 轮询 / 下载路线执行";
    }

    private static IReadOnlyList<string> BuildTextPreviewUrlList(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<string>();

        var deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Text))?.DeploymentName;
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Text))?.ModelId;

        var urls = EndpointProfileUrlBuilder.BuildConfiguredTextUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.TextApiProtocolMode,
            endpoint.IsAzureEndpoint,
            string.IsNullOrWhiteSpace(deployment) ? "{deployment}" : deployment.Trim(),
            endpoint.ApiVersion);

        return urls
            .Select(NormalizePreviewUrlForDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildImagePreviewUrlList(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<string>();

        var deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.DeploymentName;
        if (string.IsNullOrWhiteSpace(deployment))
            deployment = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.ModelId;

        var urls = EndpointProfileUrlBuilder.BuildConfiguredImageGenerateUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ImageApiRouteMode,
            deployment,
            endpoint.ApiVersion);

        return urls
            .Select(NormalizePreviewUrlForDisplay)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> BuildVideoPreviewUrlList(AiEndpoint endpoint)
    {
        var baseUrl = endpoint.BaseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<string>();

        var urls = new List<string>();

        var createUrls = EndpointProfileUrlBuilder.BuildConfiguredVideoCreateUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            VideoApiMode.Videos);
        var pollUrls = EndpointProfileUrlBuilder.BuildConfiguredVideoPollUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);
        var downloadUrls = EndpointProfileUrlBuilder.BuildConfiguredVideoDownloadUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);
        var contentVideoUrls = EndpointProfileUrlBuilder.BuildConfiguredVideoDownloadVideoContentUrlCandidates(
            baseUrl,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            "{video_id}",
            VideoApiMode.Videos);

        foreach (var createUrl in createUrls.Select(NormalizePreviewUrlForDisplay).Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"创建: {createUrl}");
        foreach (var pollUrl in pollUrls.Select(NormalizePreviewUrlForDisplay).Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"轮询: {pollUrl}");
        foreach (var downloadUrl in downloadUrls.Select(NormalizePreviewUrlForDisplay).Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"下载: {downloadUrl}");
        foreach (var contentVideoUrl in contentVideoUrls.Select(NormalizePreviewUrlForDisplay).Distinct(StringComparer.OrdinalIgnoreCase))
            urls.Add($"下载(video): {contentVideoUrl}");

        return urls;
    }

    private static IReadOnlyList<string> GetCapabilityModels(AiEndpoint endpoint, ModelCapability capability)
        => endpoint.Models
            .Where(model => model.Capabilities.HasFlag(capability))
            .Select(model => string.IsNullOrWhiteSpace(model.ModelId) ? "未填写模型 ID" : model.ModelId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string GetApiVersionDescription(AiEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ApiVersion))
            return endpoint.ApiVersion.Trim();

        return string.Empty;
    }

    private static TextApiProtocolMode GetEffectiveTextProtocol(AiEndpoint endpoint)
        => EndpointProfileUrlBuilder.GetEffectiveTextProtocol(
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.TextApiProtocolMode,
            endpoint.IsAzureEndpoint);

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

    private static string EscapeMarkdown(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("|", "\\|").Replace("\r", string.Empty).Trim();

    private static string NormalizePreviewUrlForDisplay(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value
                .Replace("%7B", "{", StringComparison.OrdinalIgnoreCase)
                .Replace("%7D", "}", StringComparison.OrdinalIgnoreCase);

    private static List<EndpointInspectionUrlItem> BuildUrlItems(IReadOnlyList<string> previewUrls)
    {
        var result = new List<EndpointInspectionUrlItem>(previewUrls.Count);
        var groupCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in previewUrls)
        {
            var groupKey = ExtractUrlGroupKey(raw);
            var nextIndex = groupCounters.TryGetValue(groupKey, out var current) ? current + 1 : 1;
            groupCounters[groupKey] = nextIndex;
            var label = nextIndex == 1 ? "地址 1*" : $"地址 {nextIndex}";
            result.Add(new EndpointInspectionUrlItem(label, raw));
        }

        return result;
    }

    private static string ExtractUrlGroupKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var colonIndex = value.IndexOf(':');
        if (colonIndex <= 0)
            return string.Empty;

        var prefix = value[..colonIndex].Trim();
        return prefix.Contains('/') || prefix.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : prefix;
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
            DefaultApiVersion = profile.Defaults.ApiVersion?.Trim() ?? string.Empty,
            IconAssetPath = profile.IconAssetPath,
            SupportsAad = profile.Defaults.SupportsAad
        };
}
