using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Storage;
using TrueFluentPro.ViewModels.Settings;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 音频任务阶段处理器 — 包含每个 AudioLifecycleStage 的具体执行逻辑。
    /// 从 AudioLabViewModel 中提取，供 AudioTaskExecutor 调用。
    /// 所有方法是纯后端逻辑，不涉及 UI 更新。
    /// </summary>
    public sealed class AudioTaskStageHandlerService
    {
        private readonly IAiInsightService _aiInsightService;
        private readonly IAzureTokenProviderStore _azureTokenProviderStore;
        private readonly IModelRuntimeResolver _modelRuntimeResolver;
        private readonly ISpeechResourceRuntimeResolver _speechResourceRuntimeResolver;
        private readonly IAiAudioTranscriptionService _aiAudioTranscriptionService;
        private readonly AudioLifecyclePipelineService _pipeline;
        private readonly ConfigurationService _configService;
        private readonly IAudioLibraryRepository _audioRepo;
        private readonly Speech.SpeechSynthesisService _ttsService;

        public AudioTaskStageHandlerService(
            IAiInsightService aiInsightService,
            IAzureTokenProviderStore azureTokenProviderStore,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            AudioLifecyclePipelineService pipeline,
            ConfigurationService configService,
            IAudioLibraryRepository audioRepo,
            Speech.SpeechSynthesisService ttsService)
        {
            _aiInsightService = aiInsightService;
            _azureTokenProviderStore = azureTokenProviderStore;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _aiAudioTranscriptionService = aiAudioTranscriptionService;
            _pipeline = pipeline;
            _configService = configService;
            _audioRepo = audioRepo;
            _ttsService = ttsService;
        }

        /// <summary>根据阶段分派到对应的处理方法。返回 AI 调用的 token 用量（非 AI 阶段返回 null）。</summary>
        public async Task<AiRequestOutcome?> ExecuteStageAsync(string audioItemId, AudioLifecycleStage stage, CancellationToken ct, Action<string>? reportProgress = null)
        {
            // 提供空操作的默认值，避免每处都判断 null
            reportProgress ??= _ => { };

            switch (stage)
            {
                case AudioLifecycleStage.Transcribed:
                    await ExecuteTranscribeAsync(audioItemId, ct, reportProgress);
                    return null; // 转录是语音服务，不产生 token
                case AudioLifecycleStage.Summarized:
                    return await ExecuteSummarizeAsync(audioItemId, ct, reportProgress);
                case AudioLifecycleStage.MindMap:
                    return await ExecuteMindMapAsync(audioItemId, ct, reportProgress);
                case AudioLifecycleStage.Insight:
                    return await ExecuteInsightAsync(audioItemId, ct, reportProgress);
                case AudioLifecycleStage.PodcastScript:
                    return await ExecutePodcastScriptAsync(audioItemId, ct, reportProgress);
                case AudioLifecycleStage.PodcastAudio:
                    await ExecutePodcastAudioAsync(audioItemId, ct, reportProgress);
                    return null; // TTS 合成，不产生 AI token
                case AudioLifecycleStage.Research:
                    return await ExecuteResearchAsync(audioItemId, ct, reportProgress);
                case AudioLifecycleStage.Translated:
                    return await ExecuteTranslatedAsync(audioItemId, ct, reportProgress);
                default:
                    throw new NotSupportedException($"阶段 {stage} 暂不支持队列化执行。");
            }
        }

        /// <summary>执行自定义阶段（非内置枚举值）：使用阶段预设的提示词调用 AI。</summary>
        public async Task<AiRequestOutcome?> ExecuteCustomStageAsync(string audioItemId, string stageKey, CancellationToken ct, Action<string>? reportProgress = null)
        {
            reportProgress ??= _ => { };
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"自定义阶段未启动：{errorMessage}");

            var preset = AudioLabStagePresetDefaults.MergeWithDefaults(config.AudioLabStagePresets)
                .FirstOrDefault(p => p.Stage == stageKey);
            if (preset == null || string.IsNullOrWhiteSpace(preset.SystemPrompt))
                throw new InvalidOperationException($"自定义阶段 {stageKey} 的提示词为空，无法执行。");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = preset.SystemPrompt;
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress($"AI 生成 {preset.DisplayName} 中（等待完整返回）...");
            var (result, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent, ct, AiChatProfile.Quick);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, result);

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException($"自定义阶段 {preset.DisplayName} 生成结果为空。");

            // 导图模式：剥除 Markdown 代码块包裹
            if (preset.DisplayMode == StageDisplayMode.MindMap)
            {
                result = result.Trim();
                if (result.StartsWith("```"))
                {
                    var firstNewline = result.IndexOf('\n');
                    if (firstNewline > 0) result = result[(firstNewline + 1)..];
                    if (result.EndsWith("```")) result = result[..^3];
                    result = result.Trim();
                }
            }

            reportProgress("保存结果...");
            _pipeline.SaveStageContent(audioItemId, stageKey, result);
            return outcome;
        }

        // ── 转录 ──────────────────────────────────────────────

        private async Task ExecuteTranscribeAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("读取音频文件信息...");
            var audioItem = GetAudioItemOrThrow(audioItemId);
            var filePath = audioItem.FilePath;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new InvalidOperationException($"音频文件不存在: {filePath}");

            reportProgress("加载配置...");
            var config = await _configService.LoadConfigAsync();

            var splitOptions = new BatchSubtitleSplitOptions
            {
                EnableSentenceSplit = config.EnableBatchSubtitleSentenceSplit,
                SplitOnComma = config.BatchSubtitleSplitOnComma,
                MaxChars = config.BatchSubtitleMaxChars,
                MaxDurationSeconds = config.BatchSubtitleMaxDurationSeconds,
                PauseSplitMs = config.BatchSubtitlePauseSplitMs
            };
            var locale = RealtimeSpeechTranscriber.GetTranscriptionSourceLanguage(
                config.AudioLabSourceLanguage ?? config.SourceLanguage);

            List<SubtitleCue> cues;
            if (config.AudioLabSpeechMode == 0)
                cues = await TranscribeWithAadAsync(filePath, config, locale, splitOptions, ct, reportProgress);
            else
                cues = await TranscribeWithTraditionalAsync(filePath, config, locale, splitOptions, ct, reportProgress);

            reportProgress("解析转录结果...");
            var segments = BuildSegmentsFromCues(cues);
            if (segments.Count == 0)
                throw new InvalidOperationException("转录完成，但未识别到任何内容。");

            reportProgress($"保存转录结果（{segments.Count} 个段落）...");
            // 序列化段落并保存到 lifecycle
            var segmentDtos = segments.Select(s => new TranscriptSegmentDto
            {
                Speaker = s.Speaker,
                SpeakerIndex = s.SpeakerIndex,
                StartTimeTicks = s.StartTime.Ticks,
                Text = s.Text,
            }).ToList();
            var json = JsonSerializer.Serialize(segmentDtos);
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Transcribed, json);
        }

        // ── 总结 ──────────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecuteSummarizeAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"总结未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "Summarized");
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成总结中（等待完整返回）...");
            var (result, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Summary,
                enableReasoning: runtimeRequest.SummaryEnableReasoning);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, result);

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("总结生成为空。");

            reportProgress("保存总结结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Summarized, result);
            return outcome;
        }

        // ── 思维导图 ──────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecuteMindMapAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out _))
                return null; // 思维导图失败不阻塞

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "MindMap");
            var userContent = $"请根据以下转录内容生成思维导图结构：\n\n{transcript}";

            reportProgress("AI 生成思维导图中（等待完整返回）...");
            var (json, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, json);

            json = json.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                if (firstNewline > 0) json = json[(firstNewline + 1)..];
                if (json.EndsWith("```")) json = json[..^3];
                json = json.Trim();
            }

            reportProgress("保存思维导图结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.MindMap, json);
            return outcome;
        }

        // ── 顿悟 ──────────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecuteInsightAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"顿悟未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "Insight");
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成顿悟分析中（等待完整返回）...");
            var (result, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, result);

            reportProgress("保存顿悟分析结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Insight, result);
            return outcome;
        }

        // ── 播客台本 ──────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecutePodcastScriptAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"播客未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "PodcastScript");
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成播客台本中（等待完整返回）...");
            var (result, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, result);

            reportProgress("保存播客台本结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.PodcastScript, result);

            // 台本变更 → 下游播客音频标记为 stale，队列才会重新提交
            _pipeline.InvalidateDownstreamStages(audioItemId, AudioLifecycleStage.PodcastScript);
            return outcome;
        }

        // ── 播客音频 TTS ──────────────────────────────────────

        private async Task ExecutePodcastAudioAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载播客台本...");
            var podcastScript = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.PodcastScript);
            if (string.IsNullOrWhiteSpace(podcastScript))
                throw new InvalidOperationException("播客台本不存在，无法合成音频。请先生成播客台本。");

            reportProgress("加载配置...");
            var config = await _configService.LoadConfigAsync();

            reportProgress("建立 TTS 认证...");
            var ttsAuth = BuildTtsAuthContext(config);

            reportProgress("加载语音列表...");
            var voices = await _ttsService.ListVoicesAsync(ttsAuth, forceRefresh: false, ct);

            reportProgress("匹配发言人语音...");
            var profiles = BuildSpeakerProfilesFromConfig(config, voices);
            if (profiles.Count == 0)
                throw new InvalidOperationException("未能为任何发言人匹配到语音。请在设置中配置播客发言人语音。");

            var outputFormat = !string.IsNullOrWhiteSpace(config.AudioLabPodcastOutputFormat)
                ? config.AudioLabPodcastOutputFormat
                : "audio-24khz-96kbitrate-mono-mp3";
            var outputDir = Path.Combine(PathManager.Instance.AppDataPath, "podcast-audio");

            reportProgress("正在合成播客音频...");
            await _pipeline.SynthesizePodcastAsync(
                ttsAuth, audioItemId, podcastScript, profiles,
                outputFormat, outputDir, ct);

            reportProgress("播客音频合成完成");
        }

        /// <summary>
        /// 从配置构建 TTS 认证上下文（AAD 或 API Key）。
        /// 逻辑与 ControlPanel.BuildTtsAuthContext 对齐。
        /// </summary>
        private Speech.SpeechSynthesisService.TtsAuthContext BuildTtsAuthContext(AzureSpeechConfig config)
        {
            // AAD 模式
            if (config.AudioLabSpeechMode == 0)
            {
                var provider = _azureTokenProviderStore.GetProvider("ai");
                if (provider != null && provider.IsLoggedIn)
                {
                    var token = provider.GetTokenAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var speechRes = FindSpeechResource(config, SpeechCapability.TextToSpeech);
                    if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.Endpoint))
                    {
                        var endpoint = speechRes.Endpoint.TrimEnd('/');
                        var isCustomDomain = endpoint.Contains(".cognitiveservices.azure.", StringComparison.OrdinalIgnoreCase);
                        return new Speech.SpeechSynthesisService.TtsAuthContext
                        {
                            AadBearerValue = $"aad#{speechRes.Id}#{token}",
                            BaseUrl = endpoint,
                            IsCustomDomainEndpoint = isCustomDomain,
                        };
                    }
                }
            }

            // 传统 API Key 模式
            {
                var speechRes = FindSpeechResource(config, SpeechCapability.TextToSpeech);
                if (speechRes != null && !string.IsNullOrWhiteSpace(speechRes.SubscriptionKey))
                {
                    var region = speechRes.ServiceRegion;
                    return new Speech.SpeechSynthesisService.TtsAuthContext
                    {
                        SubscriptionKey = speechRes.SubscriptionKey,
                        BaseUrl = $"https://{region}.tts.speech.microsoft.com",
                        IsCustomDomainEndpoint = false,
                    };
                }
            }

            throw new InvalidOperationException("无法建立 TTS 认证。请在设置中配置语音资源（AAD 或 API Key）。");
        }

        private static SpeechResource? FindSpeechResource(AzureSpeechConfig config, SpeechCapability capability)
        {
            var exact = config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled && r.Capabilities.HasFlag(capability));
            if (exact != null) return exact;
            return config.SpeechResources?
                .FirstOrDefault(r => r.IsEnabled
                    && r.Vendor == SpeechVendorType.Microsoft
                    && !string.IsNullOrWhiteSpace(r.SubscriptionKey));
        }

        /// <summary>
        /// 根据配置中的发言人语音关键字 + 语音列表，构建后端可用的 SpeakerProfile 字典。
        /// </summary>
        private static Dictionary<string, SpeakerProfile> BuildSpeakerProfilesFromConfig(
            AzureSpeechConfig config, List<VoiceInfo> voices)
        {
            var presets = new (string Tag, string Pattern)[]
            {
                ("A", config.AudioLabPodcastSpeakerAVoice ?? "XiaochenMultilingual"),
                ("B", config.AudioLabPodcastSpeakerBVoice ?? "Yunfeng"),
                ("C", config.AudioLabPodcastSpeakerCVoice ?? "Xiaoshuang"),
            };

            var profiles = new Dictionary<string, SpeakerProfile>();
            foreach (var (tag, pattern) in presets)
            {
                var match = voices.FirstOrDefault(v =>
                    v.ShortName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    var sp = new SpeakerProfile(tag, $"发言人 {tag}") { Voice = match };
                    profiles[tag] = sp;
                }
            }
            return profiles;
        }

        // ── 翻译 ──────────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecuteTranslatedAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"翻译未启动：{errorMessage}");

            var targetLang = config.TargetLanguage;
            if (string.IsNullOrWhiteSpace(targetLang))
                targetLang = "zh-Hans";

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            var systemPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "Translated")
                .Replace("{targetLang}", targetLang);
            var userContent = $"以下是需要翻译的音频转录内容：\n\n{transcript}";

            reportProgress("AI 翻译中（等待完整返回）...");
            var (result, outcome) = await aiService.ChatWithUsageAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Summary);

            if (config.AudioLabDebugMode) AttachDebug(outcome, systemPrompt, userContent, result);

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("翻译结果为空。");

            reportProgress("保存翻译结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Translated, result);
            return outcome;
        }

        // ── 深度研究 ──────────────────────────────────────────

        private async Task<AiRequestOutcome?> ExecuteResearchAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"研究未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            // Phase 1: 生成研究课题
            reportProgress("Phase 1/2：AI 规划研究课题中（等待完整返回）...");
            var (planResult, outcome1) = await aiService.ChatWithUsageAsync(
                runtimeRequest,
                "你是一个学术研究规划专家。根据音频转录内容，提出 3-5 个值得深入研究的课题。每个课题一行，格式为纯文本标题。不要编号，不要其他格式。",
                $"请根据以下内容提出研究课题：\n\n{transcript}",
                ct, AiChatProfile.Quick);

            var topicLines = planResult
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 2)
                .Take(5)
                .ToList();

            reportProgress($"Phase 1/2 完成：已规划 {topicLines.Count} 个研究课题");

            // Phase 2: 生成研究报告
            reportProgress("Phase 2/2：AI 生成深度研究报告中（等待完整返回）...");
            var selectedTopics = string.Join("\n", topicLines);
            var researchPrompt = AudioLabStagePresetDefaults.GetCustomPrompt(config.AudioLabStagePresets, "Research");
            var (result, outcome2) = await aiService.ChatWithUsageAsync(
                runtimeRequest,
                researchPrompt,
                $"研究课题：\n{selectedTopics}\n\n音频转录内容：\n{transcript}",
                ct, AiChatProfile.Summary,
                enableReasoning: runtimeRequest.SummaryEnableReasoning);

            reportProgress("保存研究报告结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Research, result);

            // 合并两次调用的 token 用量
            var merged = MergeOutcomes(outcome1, outcome2);
            if (config.AudioLabDebugMode && merged != null)
            {
                var phase1Sys = "你是一个学术研究规划专家。根据音频转录内容，提出 3-5 个值得深入研究的课题。每个课题一行，格式为纯文本标题。不要编号，不要其他格式。";
                var phase1User = $"请根据以下内容提出研究课题：\n\n{transcript}";
                var phase2User = $"研究课题：\n{selectedTopics}\n\n音频转录内容：\n{transcript}";
                merged.DebugPrompt = $"=== Phase 1 ===\n[System]\n{phase1Sys}\n\n[User]\n{phase1User}\n\n=== Phase 2 ===\n[System]\n{researchPrompt}\n\n[User]\n{phase2User}";
                merged.DebugResponse = $"=== Phase 1 (课题) ===\n{planResult}\n\n=== Phase 2 (报告) ===\n{result}";
            }
            return merged;
        }

        /// <summary>合并多次 AI 调用的 token 用量。取最后一个 ModelName，累加 token 数。</summary>
        private static AiRequestOutcome? MergeOutcomes(AiRequestOutcome? a, AiRequestOutcome? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new AiRequestOutcome
            {
                // 两个都为 null 时保持 null（表示"未知"），而非 0（表示"确认为零"）
                PromptTokens = a.PromptTokens == null && b.PromptTokens == null
                    ? null : (a.PromptTokens ?? 0) + (b.PromptTokens ?? 0),
                CompletionTokens = a.CompletionTokens == null && b.CompletionTokens == null
                    ? null : (a.CompletionTokens ?? 0) + (b.CompletionTokens ?? 0),
                ModelName = b.ModelName ?? a.ModelName,
                UsedReasoning = a.UsedReasoning || b.UsedReasoning,
                UsedFallback = a.UsedFallback || b.UsedFallback
            };
        }

        /// <summary>调试模式下将提示词和响应附加到 outcome。</summary>
        private static void AttachDebug(AiRequestOutcome? outcome, string systemPrompt, string userContent, string response)
        {
            if (outcome == null) return;
            outcome.DebugPrompt = $"[System]\n{systemPrompt}\n\n[User]\n{userContent}";
            outcome.DebugResponse = response;
        }

        // ── 私有辅助方法 ──────────────────────────────────────

        private AudioItemRecord GetAudioItemOrThrow(string audioItemId)
        {
            var item = _audioRepo.GetById(audioItemId);
            if (item == null)
                throw new InvalidOperationException($"音频项 {audioItemId} 不存在于数据库中。");
            return item;
        }

        private string LoadTranscriptTextOrThrow(string audioItemId)
        {
            var transcriptionJson = _pipeline.TryLoadCachedContent(audioItemId, AudioLifecycleStage.Transcribed);
            if (string.IsNullOrWhiteSpace(transcriptionJson))
                throw new InvalidOperationException("无转录数据，请先完成转录。");

            var segments = DeserializeTranscriptSegments(transcriptionJson);
            if (segments.Count == 0)
                throw new InvalidOperationException("转录段落为空。");

            return FormatTranscriptForAi(segments);
        }

        private static List<TranscriptSegmentDto> DeserializeTranscriptSegments(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<TranscriptSegmentDto>>(json) ?? new();
            }
            catch
            {
                return new();
            }
        }

        private static string FormatTranscriptForAi(IList<TranscriptSegmentDto> segments)
        {
            if (segments.Count == 0) return "(暂无转录内容)";
            var sb = new StringBuilder();
            foreach (var seg in segments)
            {
                var time = new TimeSpan(seg.StartTimeTicks).ToString(@"hh\:mm\:ss");
                sb.AppendLine($"[{time}] {seg.Speaker}: {seg.Text}");
            }
            return sb.ToString();
        }

        private static string GetEndpointProfileKey(AiEndpoint endpoint) => $"endpoint_{endpoint.Id}";

        private static ModelReference? SelectTextModelReference(AiConfig ai)
            => ai.ReviewModelRef ?? ai.SummaryModelRef ?? ai.QuickModelRef ?? ai.InsightModelRef;

        private bool TryBuildTextRuntimeConfig(
            AzureSpeechConfig config,
            out AiChatRequestConfig runtimeRequest,
            out AiEndpoint? endpoint,
            out string errorMessage)
        {
            runtimeRequest = new AiChatRequestConfig();
            endpoint = null;
            errorMessage = "";

            var reference = config.AudioLabTextModelRef;
            if (reference == null)
            {
                var ai = config.AiConfig;
                if (ai == null)
                {
                    errorMessage = "AI 配置不存在，请先在「设置 → 听析中心」中配置文本模型。";
                    return false;
                }
                reference = SelectTextModelReference(ai);
            }

            if (_modelRuntimeResolver.TryResolve(config, reference, ModelCapability.Text, out var runtime, out var resolveError)
                && runtime != null)
            {
                endpoint = runtime.Endpoint;
                runtimeRequest = runtime.CreateChatRequest(config.AiConfig?.SummaryEnableReasoning ?? false);
                return true;
            }

            errorMessage = string.IsNullOrWhiteSpace(resolveError)
                ? "未配置可用的文本模型，请在「设置 → 听析中心」中选择文本生成模型。"
                : resolveError;
            return false;
        }

        private async Task<AiInsightService> CreateAuthenticatedInsightServiceAsync(
            AiChatRequestConfig runtimeRequest, AiEndpoint? endpoint, CancellationToken token)
        {
            AzureTokenProvider? tokenProvider = null;
            if (runtimeRequest.AzureAuthMode == AzureAuthMode.AAD && endpoint != null)
            {
                tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                    GetEndpointProfileKey(endpoint),
                    runtimeRequest.AzureTenantId,
                    runtimeRequest.AzureClientId);
            }
            return new AiInsightService(tokenProvider);
        }

        private async Task<List<SubtitleCue>> TranscribeWithAadAsync(
            string filePath, AzureSpeechConfig config, string locale,
            BatchSubtitleSplitOptions splitOptions, CancellationToken token, Action<string> reportProgress)
        {
            var endpointId = config.AudioLabAadEndpointId;
            if (string.IsNullOrWhiteSpace(endpointId))
                throw new InvalidOperationException("听析中心未选择 AAD 终结点。");

            var endpoint = config.Endpoints.FirstOrDefault(e => e.Id == endpointId);
            if (endpoint == null || !endpoint.IsEnabled)
                throw new InvalidOperationException("听析中心选择的 AAD 终结点已不存在或已禁用。");

            if (!config.BatchStorageIsValid || string.IsNullOrWhiteSpace(config.BatchStorageConnectionString))
                throw new InvalidOperationException("存储账号未配置，批量转录需要 Azure Blob Storage。");

            reportProgress("转录：AAD 认证中...");
            var tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
                GetEndpointProfileKey(endpoint),
                endpoint.AzureTenantId,
                endpoint.AzureClientId);

            if (tokenProvider?.IsLoggedIn != true)
                throw new InvalidOperationException("AAD 认证未登录。");

            reportProgress("转录：连接存储账号...");
            var (audioContainer, _) = await BlobStorageService.GetBatchContainersAsync(
                config.BatchStorageConnectionString,
                config.BatchAudioContainerName,
                config.BatchResultContainerName,
                token);

            reportProgress("转录：上传音频到服务器...");
            var uploadedBlob = await BlobStorageService.UploadAudioToBlobAsync(filePath, audioContainer, token);
            var contentUrl = BlobStorageService.CreateBlobReadSasUri(uploadedBlob, TimeSpan.FromHours(24));

            var subdomain = AudioLabSectionVM.ParseSubdomainFromFoundryUrl(endpoint.BaseUrl);
            if (string.IsNullOrWhiteSpace(subdomain))
                throw new InvalidOperationException($"无法从终结点「{endpoint.Name}」的 URL 解析子域名。");
            var cognitiveHost = AudioLabSectionVM.IsAzureChinaUrl(endpoint.BaseUrl)
                ? $"https://{subdomain}.cognitiveservices.azure.cn"
                : $"https://{subdomain}.cognitiveservices.azure.com";
            var batchEndpoint = $"{cognitiveHost}/speechtotext/v3.1/transcriptions";

            reportProgress("转录：上传完成，提交批量转录任务...");
            var (cues, _) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                contentUrl, locale, batchEndpoint,
                async ct => await tokenProvider.GetTokenAsync(ct),
                token,
                status => reportProgress($"转录：{status}"),
                splitOptions);

            reportProgress("转录：服务器返回结果完成");
            return cues;
        }

        private async Task<List<SubtitleCue>> TranscribeWithTraditionalAsync(
            string filePath, AzureSpeechConfig config, string locale,
            BatchSubtitleSplitOptions splitOptions, CancellationToken token, Action<string> reportProgress)
        {
            AzureSubscription? subscription = null;
            var endpointId = config.AudioLabSpeechEndpointId;

            if (!string.IsNullOrWhiteSpace(endpointId))
            {
                var ep = config.Endpoints.FirstOrDefault(e => e.Id == endpointId && e.IsSpeechEndpoint && e.IsEnabled);
                if (ep != null)
                {
                    var region = !string.IsNullOrWhiteSpace(ep.SpeechRegion)
                        ? ep.SpeechRegion
                        : AzureSubscription.ParseRegionFromEndpoint(ep.SpeechEndpoint ?? "");
                    subscription = new AzureSubscription
                    {
                        Name = ep.Name,
                        SubscriptionKey = ep.SpeechSubscriptionKey,
                        ServiceRegion = region ?? "",
                        Endpoint = ep.SpeechEndpoint ?? ""
                    };
                }
            }

            if (subscription == null || !subscription.IsValid())
            {
                if (!_speechResourceRuntimeResolver.TryResolveActive(config, SpeechCapability.BatchSpeechToText, out var resolution, out var resolveError)
                    || resolution == null)
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(resolveError)
                        ? "未配置语音转写资源。"
                        : resolveError);
                }
                if (resolution.MicrosoftSubscription != null)
                    subscription = resolution.MicrosoftSubscription;
                else
                    throw new InvalidOperationException("当前语音资源不支持批量转录。");
            }

            if (!config.BatchStorageIsValid || string.IsNullOrWhiteSpace(config.BatchStorageConnectionString))
                throw new InvalidOperationException("存储账号未配置，批量转录需要 Azure Blob Storage。");

            reportProgress("转录：连接存储账号...");
            var (audioContainer, _) = await BlobStorageService.GetBatchContainersAsync(
                config.BatchStorageConnectionString,
                config.BatchAudioContainerName,
                config.BatchResultContainerName,
                token);

            reportProgress("转录：上传音频到服务器...");
            var uploadedBlob = await BlobStorageService.UploadAudioToBlobAsync(filePath, audioContainer, token);
            var contentUrl = BlobStorageService.CreateBlobReadSasUri(uploadedBlob, TimeSpan.FromHours(24));

            reportProgress("转录：上传完成，提交批量转录任务...");
            var (cues, _) = await SpeechBatchApiClient.BatchTranscribeSpeechToCuesAsync(
                contentUrl, locale, subscription, token,
                status => reportProgress($"转录：{status}"),
                splitOptions);

            reportProgress("转录：服务器返回结果完成");
            return cues;
        }

        private static List<TranscriptSegment> BuildSegmentsFromCues(IList<SubtitleCue> cues)
        {
            var segments = new List<TranscriptSegment>();
            if (cues == null || cues.Count == 0) return segments;

            var speakerMap = new Dictionary<string, int>();
            foreach (var cue in cues)
            {
                var (speaker, text) = ParseSpeaker(cue.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (!speakerMap.ContainsKey(speaker))
                    speakerMap[speaker] = speakerMap.Count;

                segments.Add(new TranscriptSegment
                {
                    Speaker = speaker,
                    SpeakerIndex = speakerMap[speaker],
                    StartTime = cue.Start,
                    Text = text,
                    SourceCues = new List<SubtitleCue> { cue }
                });
            }
            return segments;
        }

        private static (string speaker, string text) ParseSpeaker(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return ("未知", "");

            var colonIdx = raw.IndexOf(':');
            if (colonIdx < 0) colonIdx = raw.IndexOf('：');

            if (colonIdx > 0 && colonIdx < 30)
            {
                var speaker = raw[..colonIdx].Trim();
                var text = raw[(colonIdx + 1)..].Trim();
                if (!string.IsNullOrEmpty(speaker))
                    return (speaker, text);
            }

            return ("未知", raw.Trim());
        }

        /// <summary>转录段落序列化 DTO（与 AudioLabViewModel 中的格式兼容）</summary>
        private sealed class TranscriptSegmentDto
        {
            public string? Speaker { get; set; }
            public int SpeakerIndex { get; set; }
            public long StartTimeTicks { get; set; }
            public string? Text { get; set; }
        }
    }
}
