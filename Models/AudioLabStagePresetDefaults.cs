using System.Collections.Generic;
using System.Linq;

namespace TrueFluentPro.Models
{
    /// <summary>
    /// 听析中心阶段预设默认值工厂 — 提供内置默认预设和合并逻辑。
    /// </summary>
    public static class AudioLabStagePresetDefaults
    {
        /// <summary>创建内置默认预设列表（7 个阶段）。</summary>
        public static List<AudioLabStagePreset> CreateDefaults() => new()
        {
            new()
            {
                Stage = "Summarized", DisplayName = "总结",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = true,
                SystemPrompt = "你是一个专业的音频内容分析助手。请根据转录文本生成简洁的 Markdown 总结。\n\n## 概要\n用一段话概括核心内容（50-100字），标注关键时间范围。\n\n## 关键要点\n提炼 3-5 个最重要的发现或观点，每条一句话，标注 [MM:SS]。\n\n## 行动建议\n如果有值得跟进的建议或结论，列出 2-3 条。\n\n## 关键词\n提取 3-5 个关键词。\n\n注意：时间戳格式统一用 [MM:SS]，内容要简洁，不要重复。直接输出 Markdown 内容，不要用 ```markdown 代码块包裹。",
            },
            new()
            {
                Stage = "MindMap", DisplayName = "导图",
                DisplayMode = StageDisplayMode.MindMap,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = true,
                SystemPrompt = "你是一个结构化分析专家。根据音频转录文本生成思维导图的 JSON 结构。\n你必须严格输出有效 JSON，不要输出任何其他内容（不要 markdown 代码块标记）。\n格式：\n{\"label\":\"主题\",\"children\":[{\"label\":\"分支1\",\"children\":[{\"label\":\"要点1\"},{\"label\":\"要点2\"}]}]}\n层级不超过 3 层，每个分支不超过 5 个子节点。",
            },
            new()
            {
                Stage = "Insight", DisplayName = "顿悟",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = true,
                SystemPrompt = "你是一个深度思考专家。根据音频转录内容，提供深层洞察和顿悟。\n请从以下角度分析：\n1. **隐含假设**：说话者可能不自知的假设\n2. **潜在矛盾**：观点之间的冲突\n3. **深层模式**：反复出现的主题或思维模式\n4. **未说出的内容**：重要但被忽略的方面\n5. **关联启发**：与其他领域的联系\n\n以 Markdown 格式输出，标注时间戳 [HH:MM:SS]。直接输出内容，不要用代码块包裹。",
            },
            new()
            {
                Stage = "Research", DisplayName = "研究",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = true,
                SystemPrompt = "你是一个深度研究分析师。用户提供了音频转录内容和研究课题列表。请针对每个课题展开深度分析，包括核心论点和支撑证据、不同视角和反驳、与现有知识体系的关联、进一步研究建议。以 Markdown 格式输出，使用标题分隔各课题。引用时标注 [HH:MM:SS]。直接输出内容，不要用代码块包裹。",
            },
            new()
            {
                Stage = "PodcastScript", DisplayName = "播客",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = true,
                SystemPrompt = "你是一个播客脚本编写专家。根据音频转录内容，生成一段适合播客的对话内容改写。\n\n严格使用以下格式，每行一句：\n发言人 A：[主持人台词]\n发言人 B：[嘉宾台词]\n\n要求：\n1. 对话总轮次控制在 40 轮以内（A 和 B 各约 20 轮）\n2. 每轮发言控制在 200 字以内\n3. 口语化、自然过渡\n4. 不要加 Markdown 格式、括号注释或舞台指导\n5. 第一行必须是发言人 A 的开场白\n6. 突出有趣的细节和故事",
            },
            new()
            {
                Stage = "PodcastAudio", DisplayName = "播客音频",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = false, IncludeInBatch = true,
                SystemPrompt = "", // TTS 阶段无提示词
            },
            new()
            {
                Stage = "Translated", DisplayName = "翻译",
                DisplayMode = StageDisplayMode.Markdown,
                IsEnabled = true, ShowInTab = true, IncludeInBatch = false,
                SystemPrompt = "你是一个专业译者。请将以下音频转录内容完整翻译为 {targetLang}。\n\n要求：\n1. 保留原始时间戳格式 [HH:MM:SS]\n2. 保留说话人标记\n3. 译文自然流畅，忠实原文\n4. 不要添加任何注释、解释或额外内容\n5. 直接输出翻译结果，不要用代码块包裹",
            },
        };

        /// <summary>
        /// 将用户保存的预设与内置默认合并（确保每个已知阶段都存在）。
        /// 用户配置优先；用户中没有的阶段用默认值补齐；用户自定义的额外阶段追加到末尾。
        /// </summary>
        public static List<AudioLabStagePreset> MergeWithDefaults(List<AudioLabStagePreset>? userPresets)
        {
            var defaults = CreateDefaults();
            if (userPresets == null || userPresets.Count == 0)
                return defaults;

            var userMap = userPresets
                .Where(p => !string.IsNullOrWhiteSpace(p.Stage))
                .ToDictionary(p => p.Stage, p => p);

            var knownStages = new HashSet<string>(defaults.Select(d => d.Stage));

            var result = new List<AudioLabStagePreset>();
            // 先按默认顺序合并已知阶段
            foreach (var d in defaults)
            {
                if (userMap.TryGetValue(d.Stage, out var user))
                {
                    // 用户清空了提示词 → 回填内置默认
                    if (string.IsNullOrWhiteSpace(user.SystemPrompt) && !string.IsNullOrWhiteSpace(d.SystemPrompt))
                        user.SystemPrompt = d.SystemPrompt;
                    result.Add(user);
                }
                else
                {
                    result.Add(d);
                }
            }
            // 追加用户自定义的额外阶段
            foreach (var p in userPresets.Where(p => !string.IsNullOrWhiteSpace(p.Stage) && !knownStages.Contains(p.Stage)))
            {
                result.Add(p);
            }
            return result;
        }

        /// <summary>获取指定阶段是否参与 SubmitAll 批处理。</summary>
        public static bool ShouldIncludeInBatch(List<AudioLabStagePreset>? presets, string stage)
        {
            var merged = MergeWithDefaults(presets);
            var preset = merged.FirstOrDefault(p => p.Stage == stage);
            return preset != null && preset.IsEnabled && preset.IncludeInBatch;
        }

        /// <summary>获取指定阶段是否需要显示 Tab。</summary>
        public static bool ShouldShowTab(List<AudioLabStagePreset>? presets, string stage)
        {
            var merged = MergeWithDefaults(presets);
            var preset = merged.FirstOrDefault(p => p.Stage == stage);
            return preset != null && preset.IsEnabled && preset.ShowInTab;
        }

        /// <summary>获取指定阶段的 SystemPrompt（空字符串时自动回退到内置默认）。</summary>
        public static string GetCustomPrompt(List<AudioLabStagePreset>? presets, string stage)
        {
            var merged = MergeWithDefaults(presets);
            var preset = merged.FirstOrDefault(p => p.Stage == stage);
            if (preset != null && !string.IsNullOrWhiteSpace(preset.SystemPrompt))
                return preset.SystemPrompt;
            // 回退到内置默认（对自定义阶段返回空）
            var defaultPreset = CreateDefaults().FirstOrDefault(p => p.Stage == stage);
            return defaultPreset?.SystemPrompt ?? "";
        }
    }
}
