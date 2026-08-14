using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// 厂商资料包目录。该目录下的每个 *.json 都会被「自动发现」并加载为一个终结点资料包——
    /// 贡献者只需往这里丢一个符合 schema 的 JSON 文件即可，无需改动任何 C# 注册代码。
    /// 目录下所有文件已由 csproj 的 &lt;AvaloniaResource Include="Assets\**"/&gt; 自动打包进程序资源。
    /// </summary>
    private const string ProfilesFolder = "avares://TrueFluentPro/Assets/EndpointProfiles/Profiles";

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

    /// <summary>
    /// 按终结点类型查询「展示用」资料包（DisplayName / Glyph / Monogram / BadgeBackground / Subtitle / IconAssetPath）。
    /// 供 <see cref="TrueFluentPro.Models.AiEndpoint"/> 这类无法走 DI 的模型读取展示信息，使界面文案/图标完全由 profile JSON 驱动。
    /// 资料包加载失败（如设计期资源不可用）时返回 null，调用方应回退到通用默认值，绝不抛异常。
    /// </summary>
    public static EndpointProfileDefinition? GetDisplayProfile(EndpointApiType endpointType)
        => DisplayLookup.Value.TryGetValue(endpointType, out var profile) ? profile : null;

    private static readonly Lazy<IReadOnlyDictionary<EndpointApiType, EndpointProfileDefinition>> DisplayLookup =
        new(() =>
        {
            try
            {
                return LoadProfiles()
                    .GroupBy(profile => profile.EndpointType)
                    .ToDictionary(group => group.Key, group => group.First());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EndpointProfileCatalog] 加载展示资料包失败，将回退到默认展示：{ex.Message}");
                return new Dictionary<EndpointApiType, EndpointProfileDefinition>();
            }
        });

    private static IReadOnlyList<EndpointProfileDefinition> LoadProfiles()
    {
        // 自动发现：枚举 Profiles 目录下的全部 *.json（含内置与第三方贡献的厂商包）。
        var profileUris = AssetLoader
            .GetAssets(new Uri(ProfilesFolder), null)
            .Where(uri => uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(uri => uri.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profiles = new List<EndpointProfileDefinition>();
        foreach (var uri in profileUris)
        {
            // 单个厂商包损坏/格式不符时跳过并记录，不让一个坏包拖垮整个目录（插件化健壮性）。
            try
            {
                var profile = LoadFromAsset<EndpointProfileDefinition>(uri.ToString());
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    Debug.WriteLine($"[EndpointProfileCatalog] 跳过缺少 Id 的厂商资料包：{uri}");
                    continue;
                }

                profiles.Add(profile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EndpointProfileCatalog] 跳过无法解析的厂商资料包 {uri}：{ex.Message}");
            }
        }

        var ordered = profiles
            .OrderBy(profile => profile.EndpointType)
            .ToList();

        ValidateProfiles(ordered);
        return ordered;
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
