using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 搜索 Agent — 照搬 Cherry Studio 架构：
/// 单轮 LLM 意图分析（question rephraser）→ 多查询并行搜索 → JSON 引用格式化。
/// 不做评估器、不做补搜循环，靠 prompt 工程保证查询质量。
/// </summary>
public sealed class SearchAgentService
{
    /// <summary>单次搜索每条查询取多少条（默认值，可被外部覆盖）</summary>
    private const int DefaultResultsPerQuery = 5;

    // ══════════════════════════════════════════════
    //  Cherry 照搬 — 意图分析提示词（SEARCH_SUMMARY_PROMPT_WEB_ONLY）
    // ══════════════════════════════════════════════

    private const string IntentPrompt = """
        You are an AI question rephraser. Your role is to rephrase follow-up queries from a conversation into standalone queries that can be used by another LLM to retrieve information through web search.
        **Use user's language to rephrase the question.**
        Follow these guidelines:
        1. If the question is a simple writing task, greeting (e.g., Hi, Hello, How are you), or does not require searching for information (unless the greeting contains a follow-up question), return 'not_needed' in the 'question' XML block.
        2. If the user asks a question related to a specific URL, PDF, or webpage, include the links in the 'links' XML block and the question in the 'question' XML block. If the request is to summarize content from a URL or PDF, return 'summarize' in the 'question' XML block and include the relevant links in the 'links' XML block.
        3. For websearch, You need extract keywords into 'question' XML block.
        4. Always return the rephrased question inside the 'question' XML block. If there are no links in the follow-up question, do not insert a 'links' XML block.
        5. Always wrap the rephrased question in the appropriate XML blocks: use <websearch></websearch> for queries requiring real-time or external information.
        6. *use websearch to rephrase the question*

        There are several examples attached for your reference inside the below 'examples' XML block.

        <examples>
        1. Follow up question: What is the capital of France
        Rephrased question:
        <websearch>
          <question>Capital of France</question>
        </websearch>

        2. Follow up question: Hi, how are you?
        Rephrased question:
        <websearch>
          <question>not_needed</question>
        </websearch>

        3. Follow up question: What is Docker?
        Rephrased question:
        <websearch>
          <question>What is Docker</question>
        </websearch>

        4. Follow up question: Can you tell me what is X from https://example.com
        Rephrased question:
        <websearch>
          <question>What is X</question>
          <links>https://example.com</links>
        </websearch>

        5. Follow up question: Summarize the content from https://example1.com and https://example2.com
        Rephrased question:
        <websearch>
          <question>summarize</question>
          <links>https://example1.com</links>
          <links>https://example2.com</links>
        </websearch>

        6. Follow up question: Based on websearch, Which company had higher revenue in 2022, "Apple" or "Microsoft"?
        Rephrased question:
        <websearch>
          <question>Apple's revenue in 2022</question>
          <question>Microsoft's revenue in 2022</question>
        </websearch>

        7. Follow up question: Based on knowledge, Formula of Scaled Dot-Product Attention and Multi-Head Attention?
        Rephrased question:
        <websearch>
          <question>not_needed</question>
        </websearch>
        </examples>

        Anything below is part of the actual conversation. Use the conversation history and the follow-up question to rephrase the follow-up question as a standalone question based on the guidelines shared above.

        <conversation>
        {chat_history}
        </conversation>

        **Use user's language to rephrase the question.**
        Follow up question: {question}
        Rephrased question:
        """;

    // ══════════════════════════════════════════════
    //  Cherry 照搬 — REFERENCE_PROMPT
    // ══════════════════════════════════════════════

    private const string ReferencePrompt = """
        Please answer the question based on the reference materials

        ## Citation Rules:
        - Please cite the context at the end of sentences when appropriate.
        - Please use the format of citation number [number] to reference the context in corresponding parts of your answer.
        - If a sentence comes from multiple contexts, please list all relevant citation numbers, e.g., [1][2]. Remember not to group citations at the end but list them in the corresponding parts of your answer.
        - If all reference content is not relevant to the user's question, please answer based on your knowledge.

        ## My question is:

        {question}

        ## Reference Materials:

        {references}

        Please respond in the same language as the user's question.
        """;

    // ══════════════════════════════════════════════
    //  Agent 结果
    // ══════════════════════════════════════════════

    /// <summary>搜索 Agent 最终输出</summary>
    public sealed record AgentResult
    {
        public bool NeedsSearch { get; init; }
        /// <summary>所有搜索到的有效结果（去重）</summary>
        public IReadOnlyList<WebSearchResult> Results { get; init; } = [];
        /// <summary>Agent 执行的搜索查询历史</summary>
        public IReadOnlyList<string> AllQueries { get; init; } = [];
    }

    // ══════════════════════════════════════════════
    //  入口：RunAsync
    // ══════════════════════════════════════════════

    /// <summary>
    /// Cherry 风格搜索流程：
    /// 1. LLM 意图分析（提取搜索关键词）
    /// 2. 多查询并行搜索
    /// 3. 逐个抓取网页正文（Cherry 核心步骤）
    /// 4. 去重返回
    /// </summary>
    /// <summary>搜索流程各阶段回调，用于通知 UI 更新进度。</summary>
    public sealed class SearchProgress
    {
        /// <summary>意图分析完成（含搜索关键词）</summary>
        public Action<IReadOnlyList<string>>? OnIntentAnalyzed { get; init; }
        /// <summary>搜索结果页获取完成（含结果数）</summary>
        public Action<int>? OnSearchCompleted { get; init; }
        /// <summary>开始抓取网页正文</summary>
        public Action? OnFetchingContent { get; init; }
    }

    public async Task<AgentResult> RunAsync(
        string userMessage,
        string? chatHistory,
        IWebSearchProvider provider,
        AiChatRequestConfig aiConfig,
        AzureTokenProvider? tokenProvider,
        CancellationToken ct,
        bool enableIntentAnalysis = true,
        int maxResults = DefaultResultsPerQuery,
        SearchProgress? progress = null,
        AiChatRequestConfig? intentAiConfig = null)
    {
        // ── 第 1 步：意图分析（Cherry 风格 prompt）/ 直搜回退 ──
        var intent = enableIntentAnalysis
            ? await AnalyzeIntentAsync(userMessage, chatHistory, intentAiConfig ?? aiConfig, tokenProvider, ct)
            : BuildDirectSearchIntent(userMessage);

        if (!intent.NeedsSearch || intent.Questions.Count == 0)
            return new AgentResult { NeedsSearch = false };

        progress?.OnIntentAnalyzed?.Invoke(intent.Questions);

        // ── 第 2 步：多查询并行搜索 ──
        var tasks = intent.Questions.Select(q =>
            provider.SearchAsync(q, maxResults, ct));
        var batches = await Task.WhenAll(tasks);

        // ── 第 3 步：去重 ──
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueResults = batches
            .SelectMany(b => b)
            .Where(r => seen.Add(r.Url))
            .ToList();

        progress?.OnSearchCompleted?.Invoke(uniqueResults.Count);

        // ── 第 4 步：Cherry 核心 — 抓取每个 URL 的网页正文 ──
        // Cherry LocalSearchProvider: fetchWebContent(item.url, 'markdown', ...)
        progress?.OnFetchingContent?.Invoke();
        var http = WebSearchProviderFactory.GetSharedHttpClient();
        var fetcher = new WebPageFetcher(http);
        var fetchTasks = uniqueResults.Select(async r =>
        {
            var url = r.Url;
            // 如果 URL 仍是 bing.com 重定向（Base64 解码失败），用 HEAD 请求跟随重定向
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri) &&
                parsedUri.Host.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
            {
                url = await BingSearchProvider.ResolveRedirectAsync(http, url, ct);
            }
            var content = await fetcher.FetchTextAsync(url, ct);
            return r with { Url = url, Content = content };
        });
        var resultsWithContent = await Task.WhenAll(fetchTasks);

        // Cherry: results.filter(result => result.content != noContent)
        var finalResults = resultsWithContent
            .Where(r => !string.IsNullOrWhiteSpace(r.Content))
            .ToList();

        return new AgentResult
        {
            NeedsSearch = true,
            Results = finalResults.Count > 0 ? finalResults : uniqueResults.ToList(),
            AllQueries = intent.Questions
        };
    }

    // ══════════════════════════════════════════════
    //  意图分析
    // ══════════════════════════════════════════════

    internal sealed record IntentResult(
        bool NeedsSearch,
        IReadOnlyList<string> Questions,
        IReadOnlyList<string> Links,
        bool IsSummarize);

    private static IntentResult BuildDirectSearchIntent(string userMessage)
    {
        var query = userMessage?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
            return new IntentResult(false, [], [], false);

        return new IntentResult(true, [query], [], false);
    }

    private async Task<IntentResult> AnalyzeIntentAsync(
        string userMessage,
        string? chatHistory,
        AiChatRequestConfig config,
        AzureTokenProvider? tokenProvider,
        CancellationToken ct)
    {
        try
        {
            // 由外层在插入占位消息前提前快照，避免把“正在分析/搜索中”之类状态文案误当成上一轮回答。
            var normalizedChatHistory = chatHistory ?? "";

            var formattedPrompt = IntentPrompt
                .Replace("{chat_history}", normalizedChatHistory)
                .Replace("{question}", userMessage);

            var service = new AiInsightService(tokenProvider);
            var sb = new StringBuilder();
            await service.StreamChatAsync(config, formattedPrompt, "",
                chunk => sb.Append(chunk), ct, AiChatProfile.Quick);

            return ParseIntent(sb.ToString());
        }
        catch
        {
            // 意图分析失败 → fallback 为直接搜索用户原始消息
            return new IntentResult(true, [userMessage], [], false);
        }
    }

    /// <summary>解析 LLM 返回的 XML 意图（Cherry 风格 websearch 格式）</summary>
    internal static IntentResult ParseIntent(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new IntentResult(true, [], [], false);

        // 检查 not_needed
        if (response.Contains("not_needed", StringComparison.OrdinalIgnoreCase))
            return new IntentResult(false, [], [], false);

        // 解析所有 <question> 标签
        var questions = new List<string>();
        foreach (Match m in Regex.Matches(response, @"<question>\s*(.*?)\s*</question>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var q = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(q) &&
                !q.Equals("not_needed", StringComparison.OrdinalIgnoreCase))
                questions.Add(q);
        }

        // 解析 <links> 标签
        var links = new List<string>();
        foreach (Match m in Regex.Matches(response, @"<links>\s*(.*?)\s*</links>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var url = m.Groups[1].Value.Trim();
            if (Uri.TryCreate(url, UriKind.Absolute, out _))
                links.Add(url);
        }

        // 检查是否为 summarize 模式
        bool isSummarize = questions.Count == 1 &&
            questions[0].Equals("summarize", StringComparison.OrdinalIgnoreCase);

        if (questions.Count == 0 && links.Count == 0)
            return new IntentResult(false, [], [], false);

        return new IntentResult(true, questions, links, isSummarize);
    }

    // ══════════════════════════════════════════════
    //  Cherry 照搬 — JSON 格式化 + REFERENCE_PROMPT
    // ══════════════════════════════════════════════

    /// <summary>
    /// Cherry 风格结果格式化：搜索结果 → JSON + REFERENCE_PROMPT。
    /// 使用网页正文 Content（Cherry 的核心），fallback 到 Snippet。
    /// </summary>
    public static string FormatContext(AgentResult result, string userQuestion)
    {
        if (result.Results.Count == 0) return "";

        var citationData = new List<object>();
        for (int i = 0; i < result.Results.Count; i++)
        {
            var r = result.Results[i];
            citationData.Add(new
            {
                number = i + 1,
                title = r.Title,
                content = !string.IsNullOrWhiteSpace(r.Content) ? r.Content : r.Snippet,
                url = r.Url
            });
        }

        var json = JsonSerializer.Serialize(citationData,
            new JsonSerializerOptions { WriteIndented = true });
        var references = $"```json\n{json}\n```";

        return ReferencePrompt
            .Replace("{question}", userQuestion)
            .Replace("{references}", references);
    }
}
