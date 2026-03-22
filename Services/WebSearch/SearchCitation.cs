using System;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>搜索引用来源（传给 UI 展示）</summary>
public sealed record SearchCitation
{
    /// <summary>引用编号 [1], [2]...</summary>
    public int Number { get; init; }

    /// <summary>来源标题</summary>
    public required string Title { get; init; }

    /// <summary>来源 URL</summary>
    public required string Url { get; init; }

    /// <summary>摘要片段</summary>
    public string Snippet { get; init; } = "";

    /// <summary>主机名（如 docs.python.org）</summary>
    public string Hostname { get; init; } = "";

    /// <summary>从搜索结果创建引用</summary>
    public static SearchCitation FromResult(WebSearchResult result, int number)
    {
        var hostname = "";
        if (Uri.TryCreate(result.Url, UriKind.Absolute, out var uri))
            hostname = uri.Host;

        return new SearchCitation
        {
            Number = number,
            Title = result.Title,
            Url = result.Url,
            Snippet = result.Snippet,
            Hostname = hostname
        };
    }
}
