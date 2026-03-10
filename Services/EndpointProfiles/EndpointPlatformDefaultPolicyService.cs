using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public sealed class EndpointPlatformDefaultPolicyService : IEndpointPlatformDefaultPolicyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const string PolicyAsset = "avares://TrueFluentPro/Assets/EndpointProfiles/default-endpoint-policy.json";

    private readonly Lazy<EndpointPlatformDefaultCatalog> _catalog;

    public EndpointPlatformDefaultPolicyService()
    {
        _catalog = new Lazy<EndpointPlatformDefaultCatalog>(LoadCatalog);
    }

    public EndpointPlatformDefaultCatalog GetCatalog() => _catalog.Value;

    public IReadOnlyList<EndpointPlatformDefaultPolicy> GetPolicies() => _catalog.Value.Policies;

    public EndpointPlatformDefaultPolicy GetPolicy(EndpointApiType endpointType)
        => _catalog.Value.Policies.FirstOrDefault(policy => policy.EndpointType == endpointType)
           ?? throw new InvalidOperationException($"未找到终结点类型 {endpointType} 对应的平台默认值策略。\n请检查 Assets/EndpointProfiles/default-endpoint-policy.json。\n");

    private static EndpointPlatformDefaultCatalog LoadCatalog()
    {
        using var stream = AssetLoader.Open(new Uri(PolicyAsset));
        var catalog = JsonSerializer.Deserialize<EndpointPlatformDefaultCatalog>(stream, JsonOptions)
                      ?? throw new InvalidOperationException($"无法从资源 {PolicyAsset} 解析 EndpointPlatformDefaultCatalog。\n");

        ValidateCatalog(catalog);
        return catalog;
    }

    private static void ValidateCatalog(EndpointPlatformDefaultCatalog catalog)
    {
        if (catalog.Policies.Count == 0)
            throw new InvalidOperationException("平台默认值策略为空，请至少定义一个终结点策略。\n");

        var duplicateIds = catalog.Policies
            .GroupBy(policy => policy.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateIds.Count > 0)
            throw new InvalidOperationException($"平台默认值策略 ID 重复：{string.Join(", ", duplicateIds)}");

        var duplicateTypes = catalog.Policies
            .GroupBy(policy => policy.EndpointType)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateTypes.Count > 0)
            throw new InvalidOperationException($"以下终结点类型存在重复的平台默认值策略：{string.Join(", ", duplicateTypes)}");
    }
}