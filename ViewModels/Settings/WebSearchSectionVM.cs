using System.Collections.Generic;
using System.Linq;
using TrueFluentPro.Models;
using TrueFluentPro.Services.WebSearch;

namespace TrueFluentPro.ViewModels.Settings;

/// <summary>
/// 网页搜索配置分区 ViewModel。
/// </summary>
public class WebSearchSectionVM : SettingsSectionBase
{
    private string _providerId = "bing";
    private int _maxResults = 5;
    private bool _enableIntentAnalysis = true;
    private bool _enableResultCompression;

    // MCP 配置
    private string _mcpEndpoint = "";
    private string _mcpToolName = "web_search";
    private string _mcpApiKey = "";

    /// <summary>当前选中的搜索引擎 ID</summary>
    public string ProviderId
    {
        get => _providerId;
        set
        {
            if (Set(ref _providerId, value))
                OnPropertyChanged(nameof(IsMcpProvider));
        }
    }

    /// <summary>最大搜索结果数</summary>
    public int MaxResults { get => _maxResults; set => Set(ref _maxResults, value); }

    /// <summary>是否启用 AI 意图分析</summary>
    public bool EnableIntentAnalysis { get => _enableIntentAnalysis; set => Set(ref _enableIntentAnalysis, value); }

    /// <summary>是否启用结果压缩</summary>
    public bool EnableResultCompression { get => _enableResultCompression; set => Set(ref _enableResultCompression, value); }

    // MCP 配置
    public string McpEndpoint { get => _mcpEndpoint; set => Set(ref _mcpEndpoint, value); }
    public string McpToolName { get => _mcpToolName; set => Set(ref _mcpToolName, value); }
    public string McpApiKey { get => _mcpApiKey; set => Set(ref _mcpApiKey, value); }

    /// <summary>是否选择了 MCP 提供商（控制 MCP 配置区是否显示）</summary>
    public bool IsMcpProvider => _providerId == "mcp";

    /// <summary>可选的搜索引擎列表（供 UI ComboBox 绑定）</summary>
    public IReadOnlyList<ProviderOption> ProviderOptions { get; } =
        WebSearchProviderFactory.AvailableProviders
            .Select(p => new ProviderOption(p.Id, p.DisplayName))
            .ToList();

    /// <summary>当前选中的搜索引擎选项（双向绑定）</summary>
    public ProviderOption? SelectedProvider
    {
        get => ProviderOptions.FirstOrDefault(p => p.Id == _providerId)
               ?? ProviderOptions.FirstOrDefault();
        set
        {
            if (value is not null && value.Id != _providerId)
                ProviderId = value.Id;
        }
    }

    public override void LoadFrom(AzureSpeechConfig config)
    {
        ProviderId = config.WebSearchProviderId;
        MaxResults = config.WebSearchMaxResults;
        EnableIntentAnalysis = config.WebSearchEnableIntentAnalysis;
        EnableResultCompression = config.WebSearchEnableResultCompression;
        McpEndpoint = config.WebSearchMcpEndpoint;
        McpToolName = config.WebSearchMcpToolName;
        McpApiKey = config.WebSearchMcpApiKey;
    }

    public override void ApplyTo(AzureSpeechConfig config)
    {
        config.WebSearchProviderId = ProviderId;
        config.WebSearchMaxResults = MaxResults;
        config.WebSearchEnableIntentAnalysis = EnableIntentAnalysis;
        config.WebSearchEnableResultCompression = EnableResultCompression;
        config.WebSearchMcpEndpoint = McpEndpoint;
        config.WebSearchMcpToolName = McpToolName;
        config.WebSearchMcpApiKey = McpApiKey;
    }

    /// <summary>搜索引擎选项（ComboBox 项）</summary>
    public sealed record ProviderOption(string Id, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
