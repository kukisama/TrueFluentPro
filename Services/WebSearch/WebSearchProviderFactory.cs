using System;
using System.Collections.Generic;
using System.Net.Http;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 按配置的引擎 ID 创建对应的搜索提供商实例。
/// </summary>
public sealed class WebSearchProviderFactory
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36" },
            { "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.5" }
        }
    };

    /// <summary>获取共享 HttpClient（供 WebPageFetcher 等复用）</summary>
    public static HttpClient GetSharedHttpClient() => SharedHttpClient;

    /// <summary>兼容历史配置并兜底未知 providerId。</summary>
    public static string NormalizeProviderId(string? providerId)
    {
        return providerId?.Trim().ToLowerInvariant() switch
        {
            "bing" => "bing",
            "bing-cn" => "bing",
            "google" => "google",
            "bing-news" => "bing-news",
            "baidu" => "baidu",
            "duckduckgo" => "duckduckgo",
            "mcp" => "mcp",
            _ => "bing"
        };
    }

    /// <summary>所有已知的提供商 ID → 显示名映射</summary>
    public static IReadOnlyList<(string Id, string DisplayName)> AvailableProviders { get; } =
    [
        ("bing", "Bing 国际版"),
        ("google", "Google"),
        ("bing-news", "Bing 新闻"),
        ("baidu", "百度"),
        ("duckduckgo", "DuckDuckGo"),
        ("mcp", "MCP")
    ];

    /// <summary>按 providerId 创建搜索提供商</summary>
    public IWebSearchProvider Create(string providerId,
        string mcpEndpoint = "", string mcpToolName = "web_search", string? mcpApiKey = null)
    {
        return NormalizeProviderId(providerId) switch
        {
            "bing" => new BingSearchProvider(SharedHttpClient, international: true),
            "google" => new GoogleSearchProvider(SharedHttpClient),
            "bing-news" => new BingNewsSearchProvider(SharedHttpClient),
            "baidu" => new BaiduSearchProvider(SharedHttpClient),
            "duckduckgo" => new DuckDuckGoSearchProvider(SharedHttpClient),
            "mcp" => new McpSearchProvider(SharedHttpClient, mcpEndpoint, mcpToolName, mcpApiKey),
            _ => new BingSearchProvider(SharedHttpClient, international: true)
        };
    }
}
