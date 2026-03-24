using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Platform;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public sealed class EndpointProfileCatalogService : IEndpointProfileCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string[] ProfileAssets =
    [
        "avares://TrueFluentPro/Assets/EndpointProfiles/Profiles/azure-openai.json",
        "avares://TrueFluentPro/Assets/EndpointProfiles/Profiles/apim-gateway.json",
        "avares://TrueFluentPro/Assets/EndpointProfiles/Profiles/openai-compatible.json",
        "avares://TrueFluentPro/Assets/EndpointProfiles/Profiles/azure-speech.json"
    ];

    private const string InventoryAsset = "avares://TrueFluentPro/Assets/EndpointProfiles/inventory.json";

    private readonly Lazy<IReadOnlyList<EndpointProfileDefinition>> _profiles;
    private readonly Lazy<EndpointArchitectureInventory> _inventory;

    public EndpointProfileCatalogService()
    {
        _profiles = new Lazy<IReadOnlyList<EndpointProfileDefinition>>(LoadProfiles);
        _inventory = new Lazy<EndpointArchitectureInventory>(LoadInventory);
    }

    public IReadOnlyList<EndpointProfileDefinition> GetProfiles() => _profiles.Value;

    public EndpointProfileDefinition GetProfile(EndpointApiType endpointType)
        => _profiles.Value.FirstOrDefault(profile => profile.EndpointType == endpointType)
           ?? throw new InvalidOperationException($"未找到终结点类型 {endpointType} 对应的内置资料包。\n请检查 Assets/EndpointProfiles/Profiles 下的资源。\n");

    public EndpointProfileDefinition? FindProfile(string profileId)
        => _profiles.Value.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));

    public EndpointArchitectureInventory GetArchitectureInventory() => _inventory.Value;

    private static IReadOnlyList<EndpointProfileDefinition> LoadProfiles()
    {
        var profiles = ProfileAssets
            .Select(LoadFromAsset<EndpointProfileDefinition>)
            .OrderBy(profile => profile.EndpointType)
            .ToList();

        ValidateProfiles(profiles);
        return profiles;
    }

    private static EndpointArchitectureInventory LoadInventory()
        => LoadFromAsset<EndpointArchitectureInventory>(InventoryAsset);

    private static T LoadFromAsset<T>(string assetUri)
    {
        using var stream = AssetLoader.Open(new Uri(assetUri));
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new InvalidOperationException($"无法从资源 {assetUri} 解析 {typeof(T).Name}。\n");
    }

    private static void ValidateProfiles(IReadOnlyList<EndpointProfileDefinition> profiles)
    {
        if (profiles.Count == 0)
            throw new InvalidOperationException("未加载到任何终结点资料包。\n");

        var duplicateIds = profiles
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException($"终结点资料包 ID 重复：{string.Join(", ", duplicateIds)}");
        }

        var missingNames = profiles
            .Where(profile => string.IsNullOrWhiteSpace(profile.DisplayName) || string.IsNullOrWhiteSpace(profile.DefaultNamePrefix))
            .Select(profile => profile.Id)
            .ToList();

        if (missingNames.Count > 0)
        {
            throw new InvalidOperationException($"以下终结点资料包缺少 DisplayName 或 DefaultNamePrefix：{string.Join(", ", missingNames)}");
        }
    }
}
