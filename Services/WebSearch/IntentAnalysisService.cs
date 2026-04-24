using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 统一意图分析服务：在发送消息给 AI 前，判断用户主要意图。
/// 根据当前启用的能力（搜索/图像），动态构建 prompt 并返回单一意图。
/// </summary>
public sealed class IntentAnalysisService
{
    /// <summary>意图类型</summary>
    public enum IntentType
    {
        /// <summary>普通文字对话，无需工具</summary>
        Chat,
        /// <summary>需要联网搜索</summary>
        Search,
        /// <summary>需要生成或编辑图片</summary>
        Image
    }

    /// <summary>意图分析结果</summary>
    public sealed record IntentResult(IntentType Intent, string Reason = "");

    // prompt 模板：{capabilities} 和 {fallback_rule} 在运行时替换
    private const string IntentPromptTemplate = """
        你是一个意图分析助手。分析用户最新一条消息，判断用户的主要意图。

        当前可用能力：
        {capabilities}

        判断规则：
        1. 只返回一个最匹配的意图
        2. image：用户明确要求画图、生成图片、编辑图片、修改图片时选择
        3. search：用户询问事实性信息、时效性内容、需要最新数据时选择
        {fallback_rule}

        最近对话：
        {history}

        用户消息：{message}

        只返回 JSON，不要任何多余文字：{"intent": "...", "reason": "..."}
        """;

    /// <summary>
    /// 分析用户消息意图。
    /// </summary>
    /// <param name="userMessage">用户当前消息</param>
    /// <param name="chatHistory">最近几轮对话（可为空）</param>
    /// <param name="searchEnabled">搜索功能是否启用</param>
    /// <param name="imageIntentEnabled">图像意图分析是否启用</param>
    /// <param name="aiConfig">LLM 调用配置</param>
    /// <param name="tokenProvider">AAD 令牌提供者</param>
    /// <param name="ct">取消令牌</param>
    public async Task<IntentResult> AnalyzeAsync(
        string userMessage,
        string? chatHistory,
        bool searchEnabled,
        bool imageIntentEnabled,
        AiChatRequestConfig aiConfig,
        AzureTokenProvider? tokenProvider,
        CancellationToken ct)
    {
        // 两个都没开 → 直接 chat
        if (!searchEnabled && !imageIntentEnabled)
            return new IntentResult(IntentType.Chat);

        // 只开了搜索没开图像 → 不需要 LLM 判断，一律搜索
        if (searchEnabled && !imageIntentEnabled)
            return new IntentResult(IntentType.Search, "搜索已启用，图像意图未启用，默认搜索");

        // 有图像意图 → 需要 LLM 判断
        try
        {
            var capabilities = new StringBuilder();
            if (imageIntentEnabled)
                capabilities.AppendLine("- image：生成新图片或编辑/修改图片");
            if (searchEnabled)
                capabilities.AppendLine("- search：联网搜索最新信息、事实性内容");
            else
                capabilities.AppendLine("- chat：普通文字对话");

            // 搜索开启时：fallback 是搜索，不是 chat
            var fallbackRule = searchEnabled
                ? "4. 不确定时选 search（用户开启了联网搜索，说明希望获取最新信息）"
                : "4. 不确定时选 chat";

            var prompt = IntentPromptTemplate
                .Replace("{capabilities}", capabilities.ToString().TrimEnd())
                .Replace("{fallback_rule}", fallbackRule)
                .Replace("{history}", chatHistory ?? "（无）")
                .Replace("{message}", userMessage);

            var service = new AiInsightService(tokenProvider);
            var sb = new StringBuilder();
            await service.StreamChatAsync(aiConfig, prompt, "",
                chunk => sb.Append(chunk), ct, AiChatProfile.Quick);

            return ParseResult(sb.ToString(), searchEnabled);
        }
        catch
        {
            // LLM 调用失败 → fallback
            return searchEnabled
                ? new IntentResult(IntentType.Search, "意图分析失败，回退到搜索")
                : new IntentResult(IntentType.Chat, "意图分析失败，回退到对话");
        }
    }

    /// <summary>解析 LLM 返回的 JSON</summary>
    internal static IntentResult ParseResult(string response, bool searchEnabled)
    {
        if (string.IsNullOrWhiteSpace(response))
            return searchEnabled
                ? new IntentResult(IntentType.Search)
                : new IntentResult(IntentType.Chat);

        try
        {
            // 提取 JSON（LLM 可能包裹在 markdown code block 里）
            var json = response;
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                json = response[jsonStart..(jsonEnd + 1)];

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var intentStr = root.TryGetProperty("intent", out var intentProp)
                ? intentProp.GetString() ?? ""
                : "";
            var reason = root.TryGetProperty("reason", out var reasonProp)
                ? reasonProp.GetString() ?? ""
                : "";

            return intentStr.ToLowerInvariant() switch
            {
                "image" => new IntentResult(IntentType.Image, reason),
                "search" => new IntentResult(IntentType.Search, reason),
                "chat" => searchEnabled
                    ? new IntentResult(IntentType.Search, reason + "（搜索已启用，chat 提升为 search）")
                    : new IntentResult(IntentType.Chat, reason),
                _ => searchEnabled
                    ? new IntentResult(IntentType.Search, $"未识别意图: {intentStr}")
                    : new IntentResult(IntentType.Chat, $"未识别意图: {intentStr}")
            };
        }
        catch
        {
            return searchEnabled
                ? new IntentResult(IntentType.Search, "JSON 解析失败，回退搜索")
                : new IntentResult(IntentType.Chat, "JSON 解析失败，回退对话");
        }
    }
}
