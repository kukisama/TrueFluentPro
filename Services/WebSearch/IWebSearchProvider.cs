using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>搜索结果条目</summary>
public sealed record WebSearchResult
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string Snippet { get; init; } = "";
    /// <summary>网页正文内容（由 WebPageFetcher 抓取）</summary>
    public string Content { get; init; } = "";
}

/// <summary>搜索提供商接口</summary>
public interface IWebSearchProvider
{
    /// <summary>提供商唯一 ID（用于配置匹配）</summary>
    string Id { get; }

    /// <summary>显示名称</summary>
    string DisplayName { get; }

    /// <summary>执行搜索</summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults = 5, CancellationToken ct = default);
}
