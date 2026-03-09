using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.EndpointProfiles;

public static class EndpointProfileUrlBuilder
{
    public static IReadOnlyList<string> BuildConfiguredTextUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        TextApiProtocolMode configuredMode,
        bool isAzureEndpoint,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveTextApiVersion(profileId, endpointType, apiVersion, isAzureEndpoint);

        if (isAzureEndpoint)
        {
            return BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Text.DeploymentChatCompletionsUrlCandidates,
                effectiveApiVersion,
                new Dictionary<string, string?>
                {
                    ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                        ? "{deployment}"
                        : deploymentName.Trim()
                });
        }

        var protocol = ResolveConfiguredTextProtocol(profile, configuredMode);
        return protocol switch
        {
            TextApiProtocolMode.Responses => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Text.ResponsesUrlCandidates,
                effectiveApiVersion),
            TextApiProtocolMode.ChatCompletionsRaw => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Text.ChatCompletionsRawUrlCandidates,
                effectiveApiVersion),
            TextApiProtocolMode.ChatCompletionsV1 => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Text.ChatCompletionsV1UrlCandidates,
                effectiveApiVersion),
            _ => Array.Empty<string>()
        };
    }

    public static IReadOnlyList<string> BuildConfiguredAudioTranscriptionUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveCapabilityApiVersion(
            profileId,
            endpointType,
            apiVersion,
            profile.Audio.DefaultApiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            profile.Audio.TranscriptionUrlCandidates,
            effectiveApiVersion,
            new Dictionary<string, string?>
            {
                ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                    ? "{deployment}"
                    : deploymentName.Trim()
            });
    }

    public static IReadOnlyList<string> BuildConfiguredSpeechSynthesisUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveCapabilityApiVersion(
            profileId,
            endpointType,
            apiVersion,
            profile.Speech.DefaultApiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            profile.Speech.SynthesisUrlCandidates,
            effectiveApiVersion,
            new Dictionary<string, string?>
            {
                ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                    ? "{deployment}"
                    : deploymentName.Trim()
            });
    }

    public static IReadOnlyList<string> BuildImageGenerateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        if (configuredMode == ImageApiRouteMode.Auto)
        {
            foreach (var deploymentUrl in BuildConfiguredUrls(
                         normalizedBaseUrl,
                         profile?.Image.DeploymentGenerateUrlCandidates,
                         effectiveApiVersion,
                         new Dictionary<string, string?> { ["deployment"] = deploymentName }))
            {
                AddUrl(deploymentUrl);
            }

            foreach (var configuredUrl in BuildConfiguredUrls(
                         normalizedBaseUrl,
                         profile?.Image.GenerateUrlCandidates,
                         effectiveApiVersion))
            {
                AddUrl(configuredUrl);
            }

            if (urls.Count > 0)
                return urls;
        }

        foreach (var fallbackUrl in BuildFallbackImageUrls(normalizedBaseUrl, endpointType, configuredMode, effectiveApiVersion, "generations"))
            AddUrl(fallbackUrl);

        return urls;
    }

    public static IReadOnlyList<string> BuildImageGenerateUrlCandidatesForRoute(
        string baseUrl,
        string? apiVersion,
        EndpointApiType endpointType,
        ImageApiRouteMode routeMode)
        => BuildFallbackImageUrls(
            NormalizeBaseUrl(baseUrl),
            endpointType,
            routeMode,
            GetEffectiveEndpointApiVersion(null, endpointType, apiVersion),
            "generations");

    public static IReadOnlyList<string> BuildImageEditUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        if (configuredMode == ImageApiRouteMode.Auto)
        {
            foreach (var configuredUrl in BuildConfiguredUrls(
                         normalizedBaseUrl,
                         profile?.Image.EditUrlCandidates,
                         effectiveApiVersion))
            {
                AddUrl(configuredUrl);
            }

            if (urls.Count > 0)
                return urls;
        }

        foreach (var fallbackUrl in BuildFallbackImageUrls(normalizedBaseUrl, endpointType, configuredMode, effectiveApiVersion, "edits"))
            AddUrl(fallbackUrl);

        return urls;
    }

    public static IReadOnlyList<string> BuildConfiguredImageGenerateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IReadOnlyList<string> source)
        {
            foreach (var item in source)
            {
                if (seen.Add(item))
                    urls.Add(item);
            }
        }

        if (configuredMode == ImageApiRouteMode.Auto)
        {
            AddRange(BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Image.DeploymentGenerateUrlCandidates,
                effectiveApiVersion,
                new Dictionary<string, string?> { ["deployment"] = deploymentName }));
        }

        AddRange(FilterImageCandidatesByRouteMode(
            BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Image.GenerateUrlCandidates,
                effectiveApiVersion),
            configuredMode));

        return urls;
    }

    public static IReadOnlyList<string> BuildConfiguredVideoCreateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            apiMode == VideoApiMode.Videos
                ? profile.Video.CreateUrlCandidates
                : profile.Video.JobsCreateUrlCandidates,
            effectiveApiVersion);
    }

    public static IReadOnlyList<string> BuildConfiguredVideoPollUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            apiMode == VideoApiMode.Videos ? profile.Video.PollUrlCandidates : profile.Video.JobsPollUrlCandidates,
            effectiveApiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId });
    }

    public static IReadOnlyList<string> BuildConfiguredVideoDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            apiMode == VideoApiMode.Videos ? profile.Video.DownloadUrlCandidates : profile.Video.JobsDownloadUrlCandidates,
            effectiveApiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId });
    }

    public static IReadOnlyList<string> BuildConfiguredVideoDownloadVideoContentUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            apiMode == VideoApiMode.Videos
                ? profile.Video.DownloadVideoContentUrlCandidates
                : profile.Video.JobsDownloadUrlCandidates,
            effectiveApiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId });
    }

    public static TextApiProtocolMode GetEffectiveTextProtocol(
        string? profileId,
        EndpointApiType endpointType,
        TextApiProtocolMode configuredMode,
        bool isAzureEndpoint)
    {
        if (isAzureEndpoint)
            return TextApiProtocolMode.Auto;

        if (configuredMode != TextApiProtocolMode.Auto)
            return configuredMode;

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile != null
            && Enum.TryParse<TextApiProtocolMode>(profile.Text.PreferredProtocol, ignoreCase: true, out var preferred)
            && preferred != TextApiProtocolMode.Auto)
        {
            return preferred;
        }

        return endpointType == EndpointApiType.ApiManagementGateway
            ? TextApiProtocolMode.Responses
            : TextApiProtocolMode.ChatCompletionsV1;
    }

    public static string GetEffectiveEndpointApiVersion(
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion)
    {
        if (!string.IsNullOrWhiteSpace(apiVersion))
            return apiVersion.Trim();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (!string.IsNullOrWhiteSpace(profile?.Defaults.ApiVersion))
            return profile.Defaults.ApiVersion.Trim();

        return string.Empty;
    }

    public static string GetEffectiveTextApiVersion(
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        bool isAzureEndpoint)
    {
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        if (!string.IsNullOrWhiteSpace(effectiveApiVersion))
            return effectiveApiVersion;

        return isAzureEndpoint ? "2024-02-01" : string.Empty;
    }

    public static string GetEffectiveCapabilityApiVersion(
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string? capabilityDefaultApiVersion)
    {
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var endpointDefaultVersion = profile?.Defaults.ApiVersion?.Trim() ?? string.Empty;
        var capabilityDefault = capabilityDefaultApiVersion?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(apiVersion))
            return capabilityDefault;

        var explicitVersion = apiVersion.Trim();
        if (!string.IsNullOrWhiteSpace(capabilityDefault)
            && !string.IsNullOrWhiteSpace(endpointDefaultVersion)
            && string.Equals(explicitVersion, endpointDefaultVersion, StringComparison.OrdinalIgnoreCase))
        {
            return capabilityDefault;
        }

        return explicitVersion;
    }

    public static IReadOnlyList<string> BuildTextUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        TextApiProtocolMode configuredMode,
        bool isAzureEndpoint,
        string? deploymentName,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var protocol = GetEffectiveTextProtocol(profileId, endpointType, configuredMode, isAzureEndpoint);
        var effectiveApiVersion = GetEffectiveTextApiVersion(profileId, endpointType, apiVersion, isAzureEndpoint);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (seen.Add(url))
                urls.Add(url);
        }

        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return urls;

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (isAzureEndpoint)
        {
            var deploymentConfiguredUrls = BuildConfiguredUrls(
                normalizedBaseUrl,
                profile?.Text.DeploymentChatCompletionsUrlCandidates,
                effectiveApiVersion,
                new Dictionary<string, string?>
                {
                    ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                        ? "{deployment}"
                        : deploymentName.Trim()
                });

            foreach (var configuredUrl in deploymentConfiguredUrls)
                AddUrl(configuredUrl);

            if (deploymentConfiguredUrls.Count > 0)
                return urls;

            var deployment = string.IsNullOrWhiteSpace(deploymentName)
                ? "{deployment}"
                : deploymentName.Trim();

            AddUrl($"{normalizedBaseUrl}/openai/deployments/{Uri.EscapeDataString(deployment)}/chat/completions?api-version={Uri.EscapeDataString(effectiveApiVersion)}");
            return urls;
        }

        var configuredUrls = protocol switch
        {
            TextApiProtocolMode.Responses => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile?.Text.ResponsesUrlCandidates,
                effectiveApiVersion),
            TextApiProtocolMode.ChatCompletionsRaw => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile?.Text.ChatCompletionsRawUrlCandidates,
                effectiveApiVersion),
            _ => BuildConfiguredUrls(
                normalizedBaseUrl,
                profile?.Text.ChatCompletionsV1UrlCandidates,
                effectiveApiVersion)
        };

        foreach (var configuredUrl in configuredUrls)
            AddUrl(configuredUrl);

        if (configuredUrls.Count > 0)
            return urls;

        string fallbackUrl = protocol switch
        {
            TextApiProtocolMode.Responses => normalizedBaseUrl.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
                ? normalizedBaseUrl
                : $"{normalizedBaseUrl}/responses",
            TextApiProtocolMode.ChatCompletionsRaw => normalizedBaseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? normalizedBaseUrl
                : $"{normalizedBaseUrl}/chat/completions",
            _ => normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{normalizedBaseUrl}/chat/completions"
                : normalizedBaseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                    ? normalizedBaseUrl
                    : $"{normalizedBaseUrl}/v1/chat/completions"
        };

        if (!string.IsNullOrWhiteSpace(effectiveApiVersion)
            && endpointType != EndpointApiType.OpenAiCompatible)
            AddUrl(AppendApiVersion(fallbackUrl, effectiveApiVersion));

        AddUrl(fallbackUrl);
        return urls;
    }

    public static IReadOnlyList<string> BuildVideoCreateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        var configuredUrls = BuildConfiguredUrls(
            normalizedBaseUrl,
            apiMode == VideoApiMode.Videos
                ? profile?.Video.CreateUrlCandidates
                : profile?.Video.JobsCreateUrlCandidates,
            effectiveApiVersion);

        if (configuredUrls.Count > 0)
            return configuredUrls;

        if (endpointType == EndpointApiType.AzureOpenAi)
        {
            return apiMode == VideoApiMode.Videos
                ? new[] { $"{normalizedBaseUrl}/openai/v1/videos", $"{normalizedBaseUrl}/openai/v1/videos?api-version=preview" }
                : new[] { $"{normalizedBaseUrl}/openai/v1/video/generations/jobs?api-version=preview" };
        }

        var url = normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{normalizedBaseUrl}/videos"
            : $"{normalizedBaseUrl}/v1/videos";

        return endpointType != EndpointApiType.OpenAiCompatible && !string.IsNullOrWhiteSpace(effectiveApiVersion)
            ? new[] { AppendApiVersion(url, effectiveApiVersion), url }
            : new[] { url };
    }

    public static IReadOnlyList<string> BuildVideoPollUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        return BuildConfiguredOrFallbackVideoUrls(
            normalizedBaseUrl,
            profileId,
            endpointType,
            apiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId },
            profile => apiMode == VideoApiMode.Videos ? profile.Video.PollUrlCandidates : profile.Video.JobsPollUrlCandidates,
            endpointType == EndpointApiType.AzureOpenAi
                ? (apiMode == VideoApiMode.Videos
                    ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}"
                    : $"{normalizedBaseUrl}/openai/v1/video/generations/jobs/{Uri.EscapeDataString(videoId)}?api-version=preview")
                : normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? $"{normalizedBaseUrl}/videos/{Uri.EscapeDataString(videoId)}"
                    : $"{normalizedBaseUrl}/v1/videos/{Uri.EscapeDataString(videoId)}",
            endpointType == EndpointApiType.AzureOpenAi && apiMode == VideoApiMode.Videos
                ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}?api-version=preview"
                : null);
    }

    public static IReadOnlyList<string> BuildVideoDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        return BuildConfiguredOrFallbackVideoUrls(
            normalizedBaseUrl,
            profileId,
            endpointType,
            apiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId },
            profile => apiMode == VideoApiMode.Videos ? profile.Video.DownloadUrlCandidates : profile.Video.JobsDownloadUrlCandidates,
            endpointType == EndpointApiType.AzureOpenAi
                ? (apiMode == VideoApiMode.Videos
                    ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}/content"
                    : $"{normalizedBaseUrl}/openai/v1/video/generations/jobs/{Uri.EscapeDataString(videoId)}/content?api-version=preview")
                : normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? $"{normalizedBaseUrl}/videos/{Uri.EscapeDataString(videoId)}/content"
                    : $"{normalizedBaseUrl}/v1/videos/{Uri.EscapeDataString(videoId)}/content",
            endpointType == EndpointApiType.AzureOpenAi && apiMode == VideoApiMode.Videos
                ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}/content?api-version=preview"
                : null);
    }

    public static IReadOnlyList<string> BuildVideoDownloadVideoContentUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        return BuildConfiguredOrFallbackVideoUrls(
            normalizedBaseUrl,
            profileId,
            endpointType,
            apiVersion,
            new Dictionary<string, string?> { ["videoId"] = videoId },
            profile => apiMode == VideoApiMode.Videos
                ? profile.Video.DownloadVideoContentUrlCandidates
                : profile.Video.JobsDownloadUrlCandidates,
            endpointType == EndpointApiType.AzureOpenAi
                ? (apiMode == VideoApiMode.Videos
                    ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}/content/video"
                    : $"{normalizedBaseUrl}/openai/v1/video/generations/jobs/{Uri.EscapeDataString(videoId)}/content?api-version=preview")
                : normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? $"{normalizedBaseUrl}/videos/{Uri.EscapeDataString(videoId)}/content/video"
                    : $"{normalizedBaseUrl}/v1/videos/{Uri.EscapeDataString(videoId)}/content/video",
            endpointType == EndpointApiType.AzureOpenAi && apiMode == VideoApiMode.Videos
                ? $"{normalizedBaseUrl}/openai/v1/videos/{Uri.EscapeDataString(videoId)}/content/video?api-version=preview"
                : null);
    }

    public static IReadOnlyList<string> BuildVideoGenerationDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string generationId,
        bool preferVideoContent)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        return BuildConfiguredOrFallbackVideoUrls(
            normalizedBaseUrl,
            profileId,
            endpointType,
            apiVersion,
            new Dictionary<string, string?> { ["videoId"] = generationId },
            profile => preferVideoContent
                ? profile.Video.GenerationDownloadVideoContentUrlCandidates
                : profile.Video.GenerationDownloadUrlCandidates,
            endpointType == EndpointApiType.AzureOpenAi
                ? (preferVideoContent
                    ? $"{normalizedBaseUrl}/openai/v1/video/generations/{Uri.EscapeDataString(generationId)}/content/video"
                    : $"{normalizedBaseUrl}/openai/v1/video/generations/{Uri.EscapeDataString(generationId)}/content")
                : normalizedBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                    ? $"{normalizedBaseUrl}/videos/{Uri.EscapeDataString(generationId)}/content"
                    : $"{normalizedBaseUrl}/v1/videos/{Uri.EscapeDataString(generationId)}/content");
    }

    private static IReadOnlyList<string> BuildConfiguredOrFallbackVideoUrls(
        string normalizedBaseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        IReadOnlyDictionary<string, string?> replacements,
        Func<Models.EndpointProfiles.EndpointProfileDefinition, IEnumerable<string>> selector,
        string fallbackUrl,
        string? secondaryFallbackUrl = null)
    {
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        var configuredUrls = BuildConfiguredUrls(
            normalizedBaseUrl,
            profile == null ? null : selector(profile),
            effectiveApiVersion,
            replacements);

        if (configuredUrls.Count > 0)
            return configuredUrls;

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        if (endpointType != EndpointApiType.OpenAiCompatible && !string.IsNullOrWhiteSpace(effectiveApiVersion))
            AddUrl(AppendApiVersion(fallbackUrl, effectiveApiVersion));

        AddUrl(fallbackUrl);
        AddUrl(secondaryFallbackUrl);
        return urls;
    }

    private static TextApiProtocolMode ResolveConfiguredTextProtocol(
        Models.EndpointProfiles.EndpointProfileDefinition profile,
        TextApiProtocolMode configuredMode)
    {
        if (configuredMode != TextApiProtocolMode.Auto)
            return configuredMode;

        return Enum.TryParse<TextApiProtocolMode>(profile.Text.PreferredProtocol, ignoreCase: true, out var preferred)
            ? preferred
            : TextApiProtocolMode.Auto;
    }

    private static IReadOnlyList<string> BuildConfiguredUrls(
        string normalizedBaseUrl,
        IEnumerable<string>? templates,
        string? apiVersion,
        IReadOnlyDictionary<string, string?>? replacements = null)
    {
        return EndpointProfileRuntimeResolver.BuildUrls(
            normalizedBaseUrl,
            templates,
            apiVersion,
            replacements);
    }

    private static IReadOnlyList<string> BuildFallbackImageUrls(
        string normalizedBaseUrl,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? apiVersion,
        string action)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return urls;

        if (endpointType == EndpointApiType.AzureOpenAi)
        {
            AddUrl($"{normalizedBaseUrl}/openai/v1/images/{action}");
            return urls;
        }

        static string BuildOpenAiCompatibleImageUrl(string baseUrl, ImageApiRouteMode mode, string imageAction)
        {
            if (mode == ImageApiRouteMode.ImagesRaw)
            {
                return baseUrl.EndsWith($"/images/{imageAction}", StringComparison.OrdinalIgnoreCase)
                    ? baseUrl
                    : $"{baseUrl}/images/{imageAction}";
            }

            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return $"{baseUrl}/images/{imageAction}";

            return baseUrl.EndsWith($"/images/{imageAction}", StringComparison.OrdinalIgnoreCase)
                ? baseUrl
                : $"{baseUrl}/v1/images/{imageAction}";
        }

        static string AppendApiVersion(string url, string version)
        {
            if (string.IsNullOrWhiteSpace(version)
                || url.IndexOf("api-version=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return url;
            }

            var separator = url.Contains('?') ? '&' : '?';
            return $"{url}{separator}api-version={Uri.EscapeDataString(version)}";
        }

        void AddWithOptionalVersion(string url)
        {
            if (!string.IsNullOrWhiteSpace(apiVersion) && endpointType == EndpointApiType.ApiManagementGateway)
                AddUrl(AppendApiVersion(url, apiVersion.Trim()));
            AddUrl(url);
        }

        switch (configuredMode)
        {
            case ImageApiRouteMode.V1Images:
                AddWithOptionalVersion(BuildOpenAiCompatibleImageUrl(normalizedBaseUrl, ImageApiRouteMode.V1Images, action));
                break;
            case ImageApiRouteMode.ImagesRaw:
                AddWithOptionalVersion(BuildOpenAiCompatibleImageUrl(normalizedBaseUrl, ImageApiRouteMode.ImagesRaw, action));
                break;
            default:
                AddWithOptionalVersion(BuildOpenAiCompatibleImageUrl(normalizedBaseUrl, ImageApiRouteMode.V1Images, action));
                AddWithOptionalVersion(BuildOpenAiCompatibleImageUrl(normalizedBaseUrl, ImageApiRouteMode.ImagesRaw, action));
                break;
        }

        return urls;
    }

    private static IReadOnlyList<string> FilterImageCandidatesByRouteMode(
        IReadOnlyList<string> urls,
        ImageApiRouteMode routeMode)
    {
        if (routeMode == ImageApiRouteMode.Auto)
            return urls;

        return urls
            .Where(url => routeMode switch
            {
                ImageApiRouteMode.V1Images => url.IndexOf("/v1/images/", StringComparison.OrdinalIgnoreCase) >= 0
                                              || url.IndexOf("/openai/v1/images/", StringComparison.OrdinalIgnoreCase) >= 0,
                ImageApiRouteMode.ImagesRaw => url.IndexOf("/images/", StringComparison.OrdinalIgnoreCase) >= 0
                                               && url.IndexOf("/v1/images/", StringComparison.OrdinalIgnoreCase) < 0
                                               && url.IndexOf("/openai/v1/images/", StringComparison.OrdinalIgnoreCase) < 0,
                _ => true
            })
            .ToList();
    }

    private static string NormalizeBaseUrl(string? baseUrl)
        => (baseUrl ?? string.Empty).Trim().TrimEnd('/');

    private static string AppendApiVersion(string url, string apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion)
            || url.IndexOf("api-version=", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return url;
        }

        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}api-version={Uri.EscapeDataString(apiVersion)}";
    }
}