using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public static class EndpointProfileVideoModeResolver
{
    public static VideoApiMode ResolveVideoApiMode(
        string? profileId,
        EndpointApiType endpointType,
        string? modelId,
        VideoApiMode configuredMode)
    {
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType)?.Video;
        var supportedModes = profile == null
            ? new List<VideoApiMode>()
            : GetSupportedModes(profile);

        if (supportedModes.Count > 0)
        {
            var mappedMode = TryResolveMappedMode(profile!, modelId);
            if (mappedMode.HasValue && supportedModes.Contains(mappedMode.Value))
                return mappedMode.Value;

            return supportedModes.Contains(configuredMode)
                ? configuredMode
                : supportedModes[0];
        }

        try
        {
            var policy = App.Services.GetService(typeof(IEndpointPlatformDefaultPolicyService)) as IEndpointPlatformDefaultPolicyService;
            var platformPolicy = policy?.GetPolicy(endpointType);
            if (platformPolicy != null
                && Enum.TryParse<VideoApiMode>(platformPolicy.Video.DefaultMode, ignoreCase: true, out var defaultMode))
            {
                return defaultMode;
            }
        }
        catch
        {
            // 平台默认值不可用时保持旧行为。
        }

        return configuredMode;
    }

    public static VideoApiMode? TryResolveMappedMode(EndpointProfileVideoSettings profile, string? modelId)
    {
        var normalizedModel = modelId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedModel))
            return null;

        foreach (var binding in profile.ModelModeBindings)
        {
            if (!Enum.TryParse<VideoApiMode>(binding.Mode, ignoreCase: true, out var mode))
                continue;

            foreach (var pattern in binding.ModelPatterns)
            {
                if (IsPatternMatch(normalizedModel, pattern))
                    return mode;
            }
        }

        return null;
    }

    public static List<VideoApiMode> GetSupportedModes(EndpointProfileVideoSettings profile)
    {
        var modes = profile.ApiModeOptions
            .Select(option => Enum.TryParse<VideoApiMode>(option.Mode, ignoreCase: true, out var parsed)
                ? parsed
                : (VideoApiMode?)null)
            .Where(mode => mode.HasValue)
            .Select(mode => mode!.Value)
            .Distinct()
            .ToList();

        if (modes.Count > 0)
            return modes;

        return profile.SupportedApiModes
            .Select(mode => Enum.TryParse<VideoApiMode>(mode, ignoreCase: true, out var parsed)
                ? parsed
                : (VideoApiMode?)null)
            .Where(mode => mode.HasValue)
            .Select(mode => mode!.Value)
            .Distinct()
            .ToList();
    }

    private static bool IsPatternMatch(string input, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var trimmed = pattern.Trim();
        if (trimmed == "*")
            return true;

        if (trimmed.IndexOf('*') < 0)
            return string.Equals(input, trimmed, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(trimmed).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
