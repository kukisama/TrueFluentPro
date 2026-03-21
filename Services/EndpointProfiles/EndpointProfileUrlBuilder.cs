using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

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
        => BuildResolvedTextUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            configuredMode,
            isAzureEndpoint,
            deploymentName,
            apiVersion);

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
        var effectiveApiVersion = GetEffectiveCapabilityApiVersion(
            profileId,
            endpointType,
            apiVersion,
            !string.IsNullOrWhiteSpace(profile?.Overrides.Routes.Audio.DefaultApiVersion)
                ? profile!.Overrides.Routes.Audio.DefaultApiVersion
                : profile?.Audio.DefaultApiVersion);
        var replacements = new Dictionary<string, string?>
        {
            ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                ? "{deployment}"
                : deploymentName.Trim()
        };

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        if (HasVendorDefinedAudioBlock(profile) && profile is not null)
        {
            AddRange(BuildConfiguredUrls(
                normalizedBaseUrl,
                string.IsNullOrWhiteSpace(profile.Overrides.Routes.Audio.PrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { profile.Overrides.Routes.Audio.PrimaryUrl },
                effectiveApiVersion,
                replacements));

            if (string.IsNullOrWhiteSpace(profile.Overrides.Routes.Audio.PrimaryUrl))
            {
                var legacyUrls = BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Audio.TranscriptionUrlCandidates,
                    effectiveApiVersion,
                    replacements);
                if (legacyUrls.Count > 0)
                    AddUrl(legacyUrls[0]);

                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Fallbacks.Audio, effectiveApiVersion, replacements));

                foreach (var legacyUrl in legacyUrls.Skip(legacyUrls.Count > 0 ? 1 : 0))
                    AddUrl(legacyUrl);
            }
            else
            {
                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Fallbacks.Audio, effectiveApiVersion, replacements));
                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Audio.TranscriptionUrlCandidates, effectiveApiVersion, replacements));
            }

            return urls;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is null)
            return Array.Empty<string>();

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            string.IsNullOrWhiteSpace(platformPolicy.Audio.PrimaryUrl)
                ? Array.Empty<string>()
                : new[] { platformPolicy.Audio.PrimaryUrl },
            effectiveApiVersion,
            replacements);
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
        var effectiveApiVersion = GetEffectiveCapabilityApiVersion(
            profileId,
            endpointType,
            apiVersion,
            !string.IsNullOrWhiteSpace(profile?.Overrides.Routes.Speech.DefaultApiVersion)
                ? profile!.Overrides.Routes.Speech.DefaultApiVersion
                : profile?.Speech.DefaultApiVersion);
        var replacements = new Dictionary<string, string?>
        {
            ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                ? "{deployment}"
                : deploymentName.Trim()
        };

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        if (HasVendorDefinedSpeechBlock(profile) && profile is not null)
        {
            AddRange(BuildConfiguredUrls(
                normalizedBaseUrl,
                string.IsNullOrWhiteSpace(profile.Overrides.Routes.Speech.PrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { profile.Overrides.Routes.Speech.PrimaryUrl },
                effectiveApiVersion,
                replacements));

            if (string.IsNullOrWhiteSpace(profile.Overrides.Routes.Speech.PrimaryUrl))
            {
                var legacyUrls = BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Speech.SynthesisUrlCandidates,
                    effectiveApiVersion,
                    replacements);
                if (legacyUrls.Count > 0)
                    AddUrl(legacyUrls[0]);

                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Fallbacks.Speech, effectiveApiVersion, replacements));

                foreach (var legacyUrl in legacyUrls.Skip(legacyUrls.Count > 0 ? 1 : 0))
                    AddUrl(legacyUrl);
            }
            else
            {
                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Fallbacks.Speech, effectiveApiVersion, replacements));
                AddRange(BuildConfiguredUrls(normalizedBaseUrl, profile.Speech.SynthesisUrlCandidates, effectiveApiVersion, replacements));
            }

            return urls;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is null)
            return Array.Empty<string>();

        return BuildConfiguredUrls(
            normalizedBaseUrl,
            string.IsNullOrWhiteSpace(platformPolicy.Speech.PrimaryUrl)
                ? Array.Empty<string>()
                : new[] { platformPolicy.Speech.PrimaryUrl },
            effectiveApiVersion,
            replacements);
    }

    public static IReadOnlyList<string> BuildImageGenerateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? deploymentName,
        string? apiVersion)
        => BuildResolvedImageGenerateUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            configuredMode,
            deploymentName,
            apiVersion);

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
        => BuildResolvedImageEditUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            configuredMode,
            apiVersion);

    public static IReadOnlyList<string> BuildConfiguredImageGenerateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? deploymentName,
        string? apiVersion)
        => BuildResolvedImageGenerateUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            configuredMode,
            deploymentName,
            apiVersion);

    public static ApiKeyHeaderMode GetEffectiveApiKeyHeaderMode(
        string? profileId,
        EndpointApiType endpointType,
        ApiKeyHeaderMode configuredMode,
        bool isAzureEndpoint)
    {
        if (configuredMode != ApiKeyHeaderMode.Auto)
            return configuredMode;

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (HasVendorDefinedAuthBlock(profile))
        {
            var vendorMode = ResolveVendorDefaultApiKeyHeaderMode(profile!);
            if (vendorMode != ApiKeyHeaderMode.Auto)
                return vendorMode;
        }

        var platformMode = ResolvePlatformDefaultApiKeyHeaderMode(endpointType);
        if (platformMode != ApiKeyHeaderMode.Auto)
            return platformMode;

        return ApiKeyHeaderMode.Auto;
    }

    public static bool SupportsSubscriptionKeyQueryFallback(
        string? profileId,
        EndpointApiType endpointType)
    {
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (profile is null)
            return false;

        if (profile.SpecialPolicies.AllowApimSubscriptionKeyQueryRetry)
            return true;

        return profile.Auth.SupportsSubscriptionKeyQueryFallback;
    }

    public static IReadOnlyList<string> BuildConfiguredVideoCreateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        VideoApiMode apiMode)
        => BuildVideoCreateUrlCandidates(baseUrl, profileId, endpointType, apiVersion, apiMode);

    public static IReadOnlyList<string> BuildConfiguredVideoPollUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoPollUrlCandidates(baseUrl, profileId, endpointType, apiVersion, videoId, apiMode);

    public static IReadOnlyList<string> BuildConfiguredVideoDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoDownloadUrlCandidates(baseUrl, profileId, endpointType, apiVersion, videoId, apiMode);

    public static IReadOnlyList<string> BuildConfiguredVideoDownloadVideoContentUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoDownloadVideoContentUrlCandidates(baseUrl, profileId, endpointType, apiVersion, videoId, apiMode);

    public static TextApiProtocolMode GetEffectiveTextProtocol(
        string? profileId,
        EndpointApiType endpointType,
        TextApiProtocolMode configuredMode,
        bool isAzureEndpoint)
    {
        if (configuredMode != TextApiProtocolMode.Auto)
            return configuredMode;

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (HasVendorDefinedTextBlock(profile))
        {
            var vendorPreferred = ResolveVendorPreferredTextProtocol(profile!);
            if (vendorPreferred != TextApiProtocolMode.Auto)
            {
                return vendorPreferred;
            }
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy != null
            && Enum.TryParse<TextApiProtocolMode>(platformPolicy.Text.PrimaryProtocol, ignoreCase: true, out var platformPreferred)
            && platformPreferred != TextApiProtocolMode.Auto)
        {
            return platformPreferred;
        }

        return TextApiProtocolMode.Auto;
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
        if (!string.IsNullOrWhiteSpace(apiVersion))
            return apiVersion.Trim();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        if (HasVendorDefinedTextBlock(profile))
        {
            var vendorTextVersion = profile!.Overrides.Version.TextApiVersion?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(vendorTextVersion))
                return vendorTextVersion;

            var vendorEndpointVersion = profile.Defaults.ApiVersion?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(vendorEndpointVersion))
                return vendorEndpointVersion;

            return string.Empty;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        var platformVersion = platformPolicy?.Defaults.ApiVersion?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(platformVersion))
            return platformVersion;

        return string.Empty;
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

    private static IReadOnlyList<string> BuildResolvedImageGenerateUrlCandidates(
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
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        var deploymentReplacements = new Dictionary<string, string?> { ["deployment"] = deploymentName };

        if (HasVendorDefinedImageGenerateBlock(profile))
        {
            var overrideDeploymentUrls = configuredMode == ImageApiRouteMode.Auto
                ? BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(profile!.Overrides.Routes.Image.DeploymentGeneratePrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { profile.Overrides.Routes.Image.DeploymentGeneratePrimaryUrl },
                    effectiveApiVersion,
                    deploymentReplacements)
                : Array.Empty<string>();

            var overridePrimaryUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(profile!.Overrides.Routes.Image.GeneratePrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { profile.Overrides.Routes.Image.GeneratePrimaryUrl },
                    effectiveApiVersion),
                configuredMode);

            var legacyDeploymentUrls = configuredMode == ImageApiRouteMode.Auto
                ? BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Image.DeploymentGenerateUrlCandidates,
                    effectiveApiVersion,
                    deploymentReplacements)
                : Array.Empty<string>();

            var legacyGenerateUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Image.GenerateUrlCandidates,
                    effectiveApiVersion),
                configuredMode);

            var explicitFallbackUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Fallbacks.ImageGenerate,
                    effectiveApiVersion),
                configuredMode);

            AddRange(overrideDeploymentUrls);
            AddRange(overridePrimaryUrls);

            if (overrideDeploymentUrls.Count == 0 && overridePrimaryUrls.Count == 0)
            {
                if (legacyDeploymentUrls.Count > 0)
                {
                    AddUrl(legacyDeploymentUrls[0]);
                }
                else if (legacyGenerateUrls.Count > 0)
                {
                    AddUrl(legacyGenerateUrls[0]);
                }
            }

            AddRange(explicitFallbackUrls);

            foreach (var legacyUrl in legacyDeploymentUrls.Skip(overrideDeploymentUrls.Count == 0 && overridePrimaryUrls.Count == 0 && legacyDeploymentUrls.Count > 0 ? 1 : 0))
                AddUrl(legacyUrl);

            if (!(overrideDeploymentUrls.Count == 0 && overridePrimaryUrls.Count == 0 && legacyDeploymentUrls.Count > 0))
            {
                foreach (var legacyUrl in legacyGenerateUrls.Skip(overrideDeploymentUrls.Count == 0 && overridePrimaryUrls.Count == 0 && legacyGenerateUrls.Count > 0 ? 1 : 0))
                    AddUrl(legacyUrl);
            }
            else
            {
                AddRange(legacyGenerateUrls);
            }

            return urls;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is not null)
        {
            if (configuredMode == ImageApiRouteMode.Auto)
            {
                AddRange(BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(platformPolicy.Image.DeploymentGeneratePrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { platformPolicy.Image.DeploymentGeneratePrimaryUrl },
                    effectiveApiVersion,
                    deploymentReplacements));
            }

            AddRange(FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(platformPolicy.Image.GeneratePrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { platformPolicy.Image.GeneratePrimaryUrl },
                    effectiveApiVersion),
                configuredMode));

            if (urls.Count > 0)
                return urls;
        }

        return urls;
    }

    private static IReadOnlyList<string> BuildResolvedImageEditUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        ImageApiRouteMode configuredMode,
        string? apiVersion)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        if (HasVendorDefinedImageEditBlock(profile))
        {
            var overridePrimaryUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(profile!.Overrides.Routes.Image.EditPrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { profile.Overrides.Routes.Image.EditPrimaryUrl },
                    effectiveApiVersion),
                configuredMode);

            var legacyEditUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Image.EditUrlCandidates,
                    effectiveApiVersion),
                configuredMode);

            var explicitFallbackUrls = FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    profile.Fallbacks.ImageEdit,
                    effectiveApiVersion),
                configuredMode);

            AddRange(overridePrimaryUrls);

            if (overridePrimaryUrls.Count == 0 && legacyEditUrls.Count > 0)
                AddUrl(legacyEditUrls[0]);

            AddRange(explicitFallbackUrls);

            foreach (var legacyUrl in legacyEditUrls.Skip(overridePrimaryUrls.Count == 0 && legacyEditUrls.Count > 0 ? 1 : 0))
                AddUrl(legacyUrl);

            return urls;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is not null)
        {
            AddRange(FilterImageCandidatesByRouteMode(
                BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(platformPolicy.Image.EditPrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { platformPolicy.Image.EditPrimaryUrl },
                    effectiveApiVersion),
                configuredMode));

            if (urls.Count > 0)
                return urls;
        }

        return urls;
    }

    public static IReadOnlyList<string> BuildTextUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        TextApiProtocolMode configuredMode,
        bool isAzureEndpoint,
        string? deploymentName,
        string? apiVersion)
        => BuildResolvedTextUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            configuredMode,
            isAzureEndpoint,
            deploymentName,
            apiVersion);

    private static IReadOnlyList<string> BuildResolvedTextUrlCandidates(
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
        var protocol = GetEffectiveTextProtocol(profileId, endpointType, configuredMode, isAzureEndpoint);
        var effectiveApiVersion = GetEffectiveTextApiVersion(profileId, endpointType, apiVersion, isAzureEndpoint);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        var replacements = new Dictionary<string, string?>
        {
            ["deployment"] = string.IsNullOrWhiteSpace(deploymentName)
                ? "{deployment}"
                : deploymentName.Trim()
        };

        var vendorOwnsTextBlock = HasVendorDefinedTextBlock(profile);
        if (vendorOwnsTextBlock && profile is not null)
        {
            var overrideTemplates = isAzureEndpoint
                ? string.IsNullOrWhiteSpace(profile.Overrides.Routes.Text.DeploymentPrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { profile.Overrides.Routes.Text.DeploymentPrimaryUrl }
                : string.IsNullOrWhiteSpace(profile.Overrides.Routes.Text.PrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { profile.Overrides.Routes.Text.PrimaryUrl };

            var legacyUrls = BuildConfiguredUrls(
                normalizedBaseUrl,
                GetLegacyTextUrlTemplates(profile, protocol, isAzureEndpoint),
                effectiveApiVersion,
                isAzureEndpoint ? replacements : null);

            var explicitFallbackUrls = BuildConfiguredUrls(
                normalizedBaseUrl,
                profile.Fallbacks.Text,
                effectiveApiVersion,
                isAzureEndpoint ? replacements : null);

            AddRange(BuildConfiguredUrls(
                normalizedBaseUrl,
                overrideTemplates,
                effectiveApiVersion,
                isAzureEndpoint ? replacements : null));

            if (overrideTemplates.Length == 0 && legacyUrls.Count > 0)
            {
                AddUrl(legacyUrls[0]);
            }

            AddRange(explicitFallbackUrls);

            if (legacyUrls.Count > 1)
            {
                foreach (var legacyUrl in legacyUrls.Skip(1))
                {
                    AddUrl(legacyUrl);
                }
            }

            return urls;
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is not null)
        {
            var platformTemplates = isAzureEndpoint
                ? string.IsNullOrWhiteSpace(platformPolicy.Text.DeploymentPrimaryUrl)
                    ? Array.Empty<string>()
                    : new[] { platformPolicy.Text.DeploymentPrimaryUrl }
                : ShouldUsePlatformPrimaryTextRoute(platformPolicy, protocol, configuredMode)
                    ? string.IsNullOrWhiteSpace(platformPolicy.Text.PrimaryUrl)
                        ? Array.Empty<string>()
                        : new[] { platformPolicy.Text.PrimaryUrl }
                    : Array.Empty<string>();

            AddRange(BuildConfiguredUrls(
                normalizedBaseUrl,
                platformTemplates,
                effectiveApiVersion,
                isAzureEndpoint ? replacements : null));

            if (urls.Count > 0)
                return urls;
        }

        return urls;
    }

    public static IReadOnlyList<string> BuildVideoCreateUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        VideoApiMode apiMode)
        => BuildVideoUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            apiVersion,
            apiMode,
            profile => apiMode == VideoApiMode.Videos ? profile.Overrides.Routes.Video.CreatePrimaryUrl : profile.Overrides.Routes.Video.JobsCreatePrimaryUrl,
            profile => apiMode == VideoApiMode.Videos ? profile.Video.CreateUrlCandidates : Array.Empty<string>(),
            profile => apiMode == VideoApiMode.Videos ? profile.Fallbacks.VideoCreate : profile.Fallbacks.VideoJobsCreate,
            policy => apiMode == VideoApiMode.Videos ? policy.Video.CreatePrimaryUrl : policy.Video.JobsCreatePrimaryUrl,
            new Dictionary<string, string?>());

    public static IReadOnlyList<string> BuildVideoPollUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            apiVersion,
            apiMode,
            profile => apiMode == VideoApiMode.Videos ? profile.Overrides.Routes.Video.PollPrimaryUrl : profile.Overrides.Routes.Video.JobsPollPrimaryUrl,
            profile => apiMode == VideoApiMode.Videos ? profile.Video.PollUrlCandidates : Array.Empty<string>(),
            profile => apiMode == VideoApiMode.Videos ? profile.Fallbacks.VideoPoll : profile.Fallbacks.VideoJobsPoll,
            policy => apiMode == VideoApiMode.Videos ? policy.Video.PollPrimaryUrl : policy.Video.JobsPollPrimaryUrl,
            new Dictionary<string, string?> { ["videoId"] = videoId });

    public static IReadOnlyList<string> BuildVideoDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            apiVersion,
            apiMode,
            profile => apiMode == VideoApiMode.Videos ? profile.Overrides.Routes.Video.DownloadPrimaryUrl : profile.Overrides.Routes.Video.JobsDownloadPrimaryUrl,
            profile => apiMode == VideoApiMode.Videos ? profile.Video.DownloadUrlCandidates : Array.Empty<string>(),
            profile => apiMode == VideoApiMode.Videos ? profile.Fallbacks.VideoDownload : profile.Fallbacks.VideoJobsDownload,
            policy => apiMode == VideoApiMode.Videos ? policy.Video.DownloadPrimaryUrl : policy.Video.JobsDownloadPrimaryUrl,
            new Dictionary<string, string?> { ["videoId"] = videoId });

    public static IReadOnlyList<string> BuildVideoDownloadVideoContentUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string videoId,
        VideoApiMode apiMode)
        => BuildVideoUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            apiVersion,
            apiMode,
            profile => apiMode == VideoApiMode.Videos ? profile.Overrides.Routes.Video.DownloadVideoContentPrimaryUrl : profile.Overrides.Routes.Video.JobsDownloadPrimaryUrl,
            profile => apiMode == VideoApiMode.Videos
                ? profile.Video.DownloadVideoContentUrlCandidates
                : Array.Empty<string>(),
            profile => apiMode == VideoApiMode.Videos ? profile.Fallbacks.VideoDownloadVideoContent : profile.Fallbacks.VideoJobsDownload,
            policy => apiMode == VideoApiMode.Videos ? policy.Video.DownloadVideoContentPrimaryUrl : policy.Video.JobsDownloadPrimaryUrl,
            new Dictionary<string, string?> { ["videoId"] = videoId });

    public static IReadOnlyList<string> BuildVideoGenerationDownloadUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        string generationId,
        bool preferVideoContent)
        => BuildVideoUrlCandidates(
            baseUrl,
            profileId,
            endpointType,
            apiVersion,
            VideoApiMode.Videos,
            profile => preferVideoContent
                ? profile.Overrides.Routes.Video.GenerationDownloadVideoContentPrimaryUrl
                : profile.Overrides.Routes.Video.GenerationDownloadPrimaryUrl,
            profile => preferVideoContent
                ? profile.Video.GenerationDownloadVideoContentUrlCandidates
                : profile.Video.GenerationDownloadUrlCandidates,
            profile => preferVideoContent
                ? profile.Fallbacks.VideoGenerationDownloadVideoContent
                : profile.Fallbacks.VideoGenerationDownload,
            policy => preferVideoContent
                ? policy.Video.GenerationDownloadVideoContentPrimaryUrl
                : policy.Video.GenerationDownloadPrimaryUrl,
            new Dictionary<string, string?> { ["videoId"] = generationId });

    private static IReadOnlyList<string> BuildVideoUrlCandidates(
        string baseUrl,
        string? profileId,
        EndpointApiType endpointType,
        string? apiVersion,
        VideoApiMode apiMode,
        Func<EndpointProfileDefinition, string?> vendorPrimarySelector,
        Func<EndpointProfileDefinition, IEnumerable<string>> vendorLegacySelector,
        Func<EndpointProfileDefinition, IEnumerable<string>> vendorFallbackSelector,
        Func<EndpointPlatformDefaultPolicy, string> platformPrimarySelector,
        IReadOnlyDictionary<string, string?> replacements)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return Array.Empty<string>();

        var effectiveApiVersion = GetEffectiveEndpointApiVersion(profileId, endpointType, apiVersion);
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            if (seen.Add(url))
                urls.Add(url);
        }

        void AddRange(IEnumerable<string> values)
        {
            foreach (var value in values)
                AddUrl(value);
        }

        if (profile is not null)
        {
            var vendorPrimaryUrl = vendorPrimarySelector(profile);
            var legacyUrls = BuildConfiguredUrls(normalizedBaseUrl, vendorLegacySelector(profile), effectiveApiVersion, replacements);
            var explicitFallbackUrls = BuildConfiguredUrls(normalizedBaseUrl, vendorFallbackSelector(profile), effectiveApiVersion, replacements);
            var hasVendorDefinedOperation = !string.IsNullOrWhiteSpace(vendorPrimaryUrl)
                                            || legacyUrls.Count > 0
                                            || explicitFallbackUrls.Count > 0;

            if (hasVendorDefinedOperation)
            {
                AddRange(BuildConfiguredUrls(
                    normalizedBaseUrl,
                    string.IsNullOrWhiteSpace(vendorPrimaryUrl) ? Array.Empty<string>() : new[] { vendorPrimaryUrl },
                    effectiveApiVersion,
                    replacements));

                if (string.IsNullOrWhiteSpace(vendorPrimaryUrl) && legacyUrls.Count > 0)
                    AddUrl(legacyUrls[0]);

                AddRange(explicitFallbackUrls);

                foreach (var legacyUrl in legacyUrls.Skip(string.IsNullOrWhiteSpace(vendorPrimaryUrl) && legacyUrls.Count > 0 ? 1 : 0))
                    AddUrl(legacyUrl);

                return urls;
            }
        }

        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is null)
            return Array.Empty<string>();

        AddRange(BuildConfiguredUrls(
            normalizedBaseUrl,
            string.IsNullOrWhiteSpace(platformPrimarySelector(platformPolicy))
                ? Array.Empty<string>()
                : new[] { platformPrimarySelector(platformPolicy) },
            effectiveApiVersion,
            replacements));

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

    private static bool HasVendorDefinedTextBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Text.PrimaryProtocol)
               || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Text.PrimaryUrl)
               || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Text.DeploymentPrimaryUrl)
               || profile.Fallbacks.Text.Count > 0
               || !string.IsNullOrWhiteSpace(profile.Text.PreferredProtocol)
               || profile.Text.DeploymentChatCompletionsUrlCandidates.Count > 0
               || profile.Text.ResponsesUrlCandidates.Count > 0
               || profile.Text.ChatCompletionsV1UrlCandidates.Count > 0
               || profile.Text.ChatCompletionsRawUrlCandidates.Count > 0;
    }

    private static bool HasVendorDefinedAuthBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Auth.DefaultApiKeyHeaderMode)
               || profile.Overrides.Auth.AllowedApiKeyHeaderModes.Count > 0
               || !string.IsNullOrWhiteSpace(profile.Overrides.Auth.DefaultMode)
               || profile.Overrides.Auth.AllowedModes.Count > 0
               || !string.IsNullOrWhiteSpace(profile.Auth.DefaultApiKeyHeaderMode)
               || !string.IsNullOrWhiteSpace(profile.Auth.DefaultMode)
               || profile.Auth.SupportedApiKeyHeaderModes.Count > 0
               || profile.Auth.SupportedModes.Count > 0
               || profile.Auth.SupportsSubscriptionKeyQueryFallback
               || profile.Defaults.ApiKeyHeaderMode != ApiKeyHeaderMode.Auto;
    }

    private static bool HasVendorDefinedImageGenerateBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Image.GeneratePrimaryUrl)
               || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Image.DeploymentGeneratePrimaryUrl)
               || profile.Fallbacks.ImageGenerate.Count > 0
               || profile.Image.DeploymentGenerateUrlCandidates.Count > 0
               || profile.Image.GenerateUrlCandidates.Count > 0;
    }

    private static bool HasVendorDefinedImageEditBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Image.EditPrimaryUrl)
               || profile.Fallbacks.ImageEdit.Count > 0
               || profile.Image.EditUrlCandidates.Count > 0;
    }

    private static bool HasVendorDefinedVideoBlock(EndpointProfileDefinition? profile, VideoApiMode apiMode)
    {
        if (profile is null)
            return false;

        return apiMode switch
        {
            VideoApiMode.SoraJobs => !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.JobsCreatePrimaryUrl)
                                     || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.JobsPollPrimaryUrl)
                                     || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.JobsDownloadPrimaryUrl)
                                     || profile.Fallbacks.VideoJobsCreate.Count > 0
                                     || profile.Fallbacks.VideoJobsPoll.Count > 0
                                     || profile.Fallbacks.VideoJobsDownload.Count > 0,
            _ => !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.CreatePrimaryUrl)
                 || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.PollPrimaryUrl)
                 || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.DownloadPrimaryUrl)
                 || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.DownloadVideoContentPrimaryUrl)
                 || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.GenerationDownloadPrimaryUrl)
                 || !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Video.GenerationDownloadVideoContentPrimaryUrl)
                 || profile.Fallbacks.VideoCreate.Count > 0
                 || profile.Fallbacks.VideoPoll.Count > 0
                 || profile.Fallbacks.VideoDownload.Count > 0
                 || profile.Fallbacks.VideoDownloadVideoContent.Count > 0
                 || profile.Fallbacks.VideoGenerationDownload.Count > 0
                 || profile.Fallbacks.VideoGenerationDownloadVideoContent.Count > 0
                 || profile.Video.CreateUrlCandidates.Count > 0
                 || profile.Video.PollUrlCandidates.Count > 0
                 || profile.Video.DownloadUrlCandidates.Count > 0
                 || profile.Video.DownloadVideoContentUrlCandidates.Count > 0
                 || profile.Video.GenerationDownloadUrlCandidates.Count > 0
                 || profile.Video.GenerationDownloadVideoContentUrlCandidates.Count > 0
        };
    }

    private static ApiKeyHeaderMode ResolveVendorDefaultApiKeyHeaderMode(EndpointProfileDefinition profile)
    {
        if (Enum.TryParse<ApiKeyHeaderMode>(profile.Overrides.Auth.DefaultApiKeyHeaderMode, ignoreCase: true, out var overrideMode))
            return overrideMode;

        if (profile.Defaults.ApiKeyHeaderMode != ApiKeyHeaderMode.Auto)
            return profile.Defaults.ApiKeyHeaderMode;

        if (Enum.TryParse<ApiKeyHeaderMode>(profile.Auth.DefaultApiKeyHeaderMode, ignoreCase: true, out var authMode))
            return authMode;

        return ApiKeyHeaderMode.Auto;
    }

    private static ApiKeyHeaderMode ResolvePlatformDefaultApiKeyHeaderMode(EndpointApiType endpointType)
    {
        var platformPolicy = TryGetPlatformDefaultPolicy(endpointType);
        if (platformPolicy is null)
            return ApiKeyHeaderMode.Auto;

        if (Enum.TryParse<ApiKeyHeaderMode>(platformPolicy.Auth.DefaultApiKeyHeaderMode, ignoreCase: true, out var authMode))
            return authMode;

        return platformPolicy.Defaults.ApiKeyHeaderMode;
    }

    private static TextApiProtocolMode ResolveVendorPreferredTextProtocol(EndpointProfileDefinition profile)
    {
        if (Enum.TryParse<TextApiProtocolMode>(profile.Overrides.Routes.Text.PrimaryProtocol, ignoreCase: true, out var overrideProtocol))
        {
            return overrideProtocol;
        }

        if (Enum.TryParse<TextApiProtocolMode>(profile.Text.PreferredProtocol, ignoreCase: true, out var legacyPreferred))
        {
            return legacyPreferred;
        }

        if (profile.Text.ResponsesUrlCandidates.Count > 0)
            return TextApiProtocolMode.Responses;

        if (profile.Text.ChatCompletionsRawUrlCandidates.Count > 0)
            return TextApiProtocolMode.ChatCompletionsRaw;

        if (profile.Text.ChatCompletionsV1UrlCandidates.Count > 0)
            return TextApiProtocolMode.ChatCompletionsV1;

        return TextApiProtocolMode.Auto;
    }

    private static bool HasVendorDefinedAudioBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Audio.PrimaryUrl)
               || profile.Fallbacks.Audio.Count > 0
               || profile.Audio.TranscriptionUrlCandidates.Count > 0;
    }

    private static bool HasVendorDefinedSpeechBlock(EndpointProfileDefinition? profile)
    {
        if (profile is null)
            return false;

        return !string.IsNullOrWhiteSpace(profile.Overrides.Routes.Speech.PrimaryUrl)
               || profile.Fallbacks.Speech.Count > 0
               || profile.Speech.SynthesisUrlCandidates.Count > 0;
    }

    private static IEnumerable<string> GetLegacyTextUrlTemplates(
        EndpointProfileDefinition profile,
        TextApiProtocolMode protocol,
        bool isAzureEndpoint)
    {
        if (isAzureEndpoint)
            return profile.Text.DeploymentChatCompletionsUrlCandidates;

        return protocol switch
        {
            TextApiProtocolMode.Responses => profile.Text.ResponsesUrlCandidates,
            TextApiProtocolMode.ChatCompletionsRaw => profile.Text.ChatCompletionsRawUrlCandidates,
            _ => profile.Text.ChatCompletionsV1UrlCandidates
        };
    }

    private static bool ShouldUsePlatformPrimaryTextRoute(
        EndpointPlatformDefaultPolicy platformPolicy,
        TextApiProtocolMode protocol,
        TextApiProtocolMode configuredMode)
    {
        if (!Enum.TryParse<TextApiProtocolMode>(platformPolicy.Text.PrimaryProtocol, ignoreCase: true, out var platformProtocol))
        {
            return configuredMode == TextApiProtocolMode.Auto;
        }

        return configuredMode == TextApiProtocolMode.Auto || platformProtocol == protocol;
    }

    private static EndpointPlatformDefaultPolicy? TryGetPlatformDefaultPolicy(EndpointApiType endpointType)
    {
        try
        {
            var service = App.Services?.GetService<IEndpointPlatformDefaultPolicyService>();
            return service?.GetPolicy(endpointType);
        }
        catch
        {
            return null;
        }
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