using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public static class EndpointCapabilityPolicyResolver
{
    private static readonly ModelCapability[] AllCapabilities =
    {
        ModelCapability.Text,
        ModelCapability.Image,
        ModelCapability.Video,
        ModelCapability.SpeechToText,
        ModelCapability.TextToSpeech
    };

    public static IReadOnlyList<ModelCapability> KnownCapabilities => AllCapabilities;

    public static bool IsCapabilityAllowed(
        string? profileId,
        EndpointApiType endpointType,
        ModelCapability capability)
        => GetAllowedCapabilities(profileId, endpointType).Contains(capability);

    public static ModelCapability ApplyCapabilityPolicy(
        string? profileId,
        EndpointApiType endpointType,
        ModelCapability capabilities)
    {
        var allowed = GetAllowedCapabilities(profileId, endpointType);
        var filtered = (ModelCapability)0;

        foreach (var capability in AllCapabilities)
        {
            if (capabilities.HasFlag(capability) && allowed.Contains(capability))
            {
                filtered |= capability;
            }
        }

        return filtered;
    }

    public static IReadOnlyCollection<ModelCapability> GetAllowedCapabilities(
        string? profileId,
        EndpointApiType endpointType)
    {
        var allowed = GetPlatformAllowedCapabilities(endpointType);
        var profile = EndpointProfileRuntimeResolver.Resolve(profileId, endpointType);
        var vendorPolicy = ResolveVendorPolicy(profile);

        return vendorPolicy == null
            ? allowed
            : ApplyPolicy(vendorPolicy, allowed);
    }

    private static HashSet<ModelCapability> GetPlatformAllowedCapabilities(EndpointApiType endpointType)
    {
        try
        {
            var service = App.Services.GetService(typeof(IEndpointPlatformDefaultPolicyService)) as IEndpointPlatformDefaultPolicyService;
            var policy = service?.GetPolicy(endpointType);
            if (policy == null)
            {
                return new HashSet<ModelCapability>(AllCapabilities);
            }

            return ApplyPolicy(policy.Capabilities, new HashSet<ModelCapability>(AllCapabilities));
        }
        catch
        {
            return new HashSet<ModelCapability>(AllCapabilities);
        }
    }

    private static EndpointCapabilityPolicy? ResolveVendorPolicy(EndpointProfileDefinition? profile)
    {
        if (profile == null)
            return null;

        if (HasExplicitPolicy(profile.Overrides.Capabilities))
            return profile.Overrides.Capabilities;

        return HasExplicitPolicy(profile.Capabilities)
            ? profile.Capabilities
            : null;
    }

    private static bool HasExplicitPolicy(EndpointCapabilityPolicy policy)
        => policy.Mode != EndpointCapabilityPolicyMode.InheritDefault
           || policy.Allowed.Count > 0
           || policy.Disabled.Count > 0;

    private static HashSet<ModelCapability> ApplyPolicy(
        EndpointCapabilityPolicy policy,
        HashSet<ModelCapability> baseAllowed)
    {
        return policy.Mode switch
        {
            EndpointCapabilityPolicyMode.AllowOnly => ParseCapabilities(policy.Allowed),
            EndpointCapabilityPolicyMode.DenySome => ApplyDisabled(baseAllowed, policy.Disabled),
            _ => baseAllowed
        };
    }

    private static HashSet<ModelCapability> ApplyDisabled(
        HashSet<ModelCapability> baseAllowed,
        IEnumerable<string> disabled)
    {
        var result = new HashSet<ModelCapability>(baseAllowed);
        foreach (var capability in ParseCapabilities(disabled))
        {
            result.Remove(capability);
        }

        return result;
    }

    private static HashSet<ModelCapability> ParseCapabilities(IEnumerable<string> values)
    {
        var result = new HashSet<ModelCapability>();
        foreach (var value in values)
        {
            if (TryParseCapability(value, out var capability))
            {
                result.Add(capability);
            }
        }

        return result;
    }

    private static bool TryParseCapability(string? value, out ModelCapability capability)
    {
        capability = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return Enum.TryParse(normalized, ignoreCase: true, out capability);
    }
}
