using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models
{
    public enum AiProviderType
    {
        OpenAiCompatible,
        AzureOpenAi
    }

    public enum AiChatProfile
    {
        Quick,
        Summary
    }

    public enum AzureAuthMode
    {
        ApiKey,
        AAD
    }

    public class InsightPresetButton
    {
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
    }

    public class AiConfig
    {
        public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;

        public string ApiEndpoint { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ModelName { get; set; } = "gpt-4o-mini";
        public string SummaryModelName { get; set; } = "";
        public string QuickModelName { get; set; } = "";

        public string DeploymentName { get; set; } = "";
        public string SummaryDeploymentName { get; set; } = "";
        public string QuickDeploymentName { get; set; } = "";
        public string ApiVersion { get; set; } = "2024-02-01";

        // --- AAD 认证 ---
        public AzureAuthMode AzureAuthMode { get; set; } = AzureAuthMode.ApiKey;
        public string AzureTenantId { get; set; } = "";
        public string AzureClientId { get; set; } = "";

        public bool SummaryEnableReasoning { get; set; } = false;

        public string InsightSystemPrompt { get; set; } = "你是一个专业的会议/翻译分析助手。用户会提供实时翻译的历史记录，请根据用户的问题对内容进行分析。请用 Markdown 格式输出分析结果。";

        public string ReviewSystemPrompt { get; set; } = "你是一个会议复盘助手。根据字幕内容生成结构化 Markdown 总结。请输出包含关键结论、行动项和风险点，并在引用内容时标注时间戳，格式为 [HH:MM:SS]。";

        public string InsightUserContentTemplate { get; set; } = "以下是翻译历史记录：\n\n{history}\n\n---\n\n用户问题：{question}";

        public string ReviewUserContentTemplate { get; set; } = "以下是会议字幕内容:\n\n{subtitle}\n\n---\n\n{prompt}";

        // --- 终结点模型引用（3.4 新增，与旧字段并存以便迁移） ---
        public ModelReference? InsightModelRef { get; set; }
        public ModelReference? SummaryModelRef { get; set; }
        public ModelReference? QuickModelRef { get; set; }
        public ModelReference? ReviewModelRef { get; set; }

        public bool AutoInsightBufferOutput { get; set; } = true;

        public List<InsightPresetButton> PresetButtons { get; set; } = new()
        {
            new() { Name = "会议摘要", Prompt = "请对以上翻译记录进行会议摘要。总结会议的主要议题、关键讨论内容和结论。" },
            new() { Name = "知识点提取", Prompt = "请从以上翻译记录中提取核心知识点和专业术语，按主题分类整理。" },
            new() { Name = "客户投诉识别", Prompt = "请识别以上翻译记录中是否存在客户投诉、不满或负面反馈，列出具体内容和建议的应对方式。" },
            new() { Name = "行动项提取", Prompt = "请从以上翻译记录中提取所有行动项(Action Items)，包括待办事项、分工安排、承诺和截止时间。" },
            new() { Name = "情绪分析", Prompt = "请对以上翻译记录进行情绪分析，判断对话中各参与者的整体情绪倾向，标注情绪变化的关键节点。" },
        };

        public List<ReviewSheetPreset> ReviewSheets { get; set; } = new()
        {
            new()
            {
                Name = "总结复盘",
                FileTag = "summary",
                Prompt = "请基于字幕内容生成结构化会议总结，包含关键结论、行动项与风险点，并在引用内容时标注时间戳，格式为 [HH:MM:SS]。"
            },
            new()
            {
                Name = "情绪复盘",
                FileTag = "emotion",
                Prompt = "请分析对话情绪走向，指出情绪变化的关键时间点与可能原因，并标注时间戳 [HH:MM:SS]。"
            },
            new()
            {
                Name = "客户顾虑",
                FileTag = "customer",
                Prompt = "请识别客户提出的疑虑、问题与期望后续动作，按主题整理，并标注时间戳 [HH:MM:SS]。"
            },
            new()
            {
                Name = "知识点复盘",
                FileTag = "knowledge",
                Prompt = "请提取关键知识点与术语，并给出简要解释或背景说明，标注时间戳 [HH:MM:SS]。"
            }
        };

        [JsonIgnore]
        public bool IsValid => !string.IsNullOrWhiteSpace(ApiEndpoint)
                            && (AzureAuthMode == AzureAuthMode.AAD || !string.IsNullOrWhiteSpace(ApiKey))
                            && (ProviderType == AiProviderType.OpenAiCompatible
                                ? !string.IsNullOrWhiteSpace(ModelName)
                                    || !string.IsNullOrWhiteSpace(SummaryModelName)
                                    || !string.IsNullOrWhiteSpace(QuickModelName)
                                : !string.IsNullOrWhiteSpace(DeploymentName)
                                    || !string.IsNullOrWhiteSpace(SummaryDeploymentName)
                                    || !string.IsNullOrWhiteSpace(QuickDeploymentName));

        public string GetModelName(AiChatProfile profile)
        {
            if (profile == AiChatProfile.Summary)
            {
                if (!string.IsNullOrWhiteSpace(SummaryModelName))
                    return SummaryModelName;
                if (!string.IsNullOrWhiteSpace(QuickModelName))
                    return QuickModelName;
                return ModelName;
            }

            if (!string.IsNullOrWhiteSpace(QuickModelName))
                return QuickModelName;
            if (!string.IsNullOrWhiteSpace(SummaryModelName))
                return SummaryModelName;
            return ModelName;
        }

        public string GetDeploymentName(AiChatProfile profile)
        {
            if (profile == AiChatProfile.Summary)
            {
                if (!string.IsNullOrWhiteSpace(SummaryDeploymentName))
                    return SummaryDeploymentName;
                if (!string.IsNullOrWhiteSpace(QuickDeploymentName))
                    return QuickDeploymentName;
                return DeploymentName;
            }

            if (!string.IsNullOrWhiteSpace(QuickDeploymentName))
                return QuickDeploymentName;
            if (!string.IsNullOrWhiteSpace(SummaryDeploymentName))
                return SummaryDeploymentName;
            return DeploymentName;
        }
    }
}
