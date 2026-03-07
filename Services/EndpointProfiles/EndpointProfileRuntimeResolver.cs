using System;
using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public static class EndpointProfileRuntimeResolver
{
    private static readonly Lazy<EndpointProfileCatalogService> Catalog = new(() => new EndpointProfileCatalogService());

    public static EndpointProfileDefinition? Resolve(string? profileId, EndpointApiType endpointType)
    {
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var byId = Catalog.Value.FindProfile(profileId.Trim());
            if (byId != null)
                return byId;
        }

        try
        {
            return Catalog.Value.GetProfile(endpointType);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> BuildUrls(
        string baseUrl,
        IEnumerable<string>? urlTemplates,
        string? apiVersion = null,
        bool appendApiVersionWhenPresent = false,
        IReadOnlyDictionary<string, string?>? replacements = null)
    {
        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedBaseUrl = (baseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (string.IsNullOrWhiteSpace(normalizedBaseUrl))
            return urls;

        foreach (var template in urlTemplates ?? Enumerable.Empty<string>())
        {
            var expanded = ExpandUrlTemplate(normalizedBaseUrl, template, replacements);
            if (string.IsNullOrWhiteSpace(expanded))
                continue;

            if (appendApiVersionWhenPresent && !string.IsNullOrWhiteSpace(apiVersion))
            {
                expanded = AppendApiVersion(expanded, apiVersion.Trim());
            }

            if (seen.Add(expanded))
            {
                urls.Add(expanded);
            }
        }

        return urls;
    }

    private static string? ExpandUrlTemplate(
        string baseUrl,
        string? template,
        IReadOnlyDictionary<string, string?>? replacements)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;

        var expanded = template.Trim();
        expanded = expanded.Replace("{baseUrl}", baseUrl, StringComparison.OrdinalIgnoreCase);

        foreach (var pair in replacements ?? new Dictionary<string, string?>())
        {
            var placeholder = $"{{{pair.Key}}}";
            if (expanded.IndexOf(placeholder, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (string.IsNullOrWhiteSpace(pair.Value))
                return null;

            expanded = expanded.Replace(placeholder, Uri.EscapeDataString(pair.Value.Trim()), StringComparison.OrdinalIgnoreCase);
        }

        if (expanded.Contains('{') || expanded.Contains('}'))
            return null;

        if (expanded.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || expanded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return expanded;
        }

        var relative = expanded.TrimStart('/');
        return $"{baseUrl}/{relative}";
    }

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