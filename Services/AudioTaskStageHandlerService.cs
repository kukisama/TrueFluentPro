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

        public AudioTaskStageHandlerService(
            IAiInsightService aiInsightService,
            IAzureTokenProviderStore azureTokenProviderStore,
            IModelRuntimeResolver modelRuntimeResolver,
            ISpeechResourceRuntimeResolver speechResourceRuntimeResolver,
            IAiAudioTranscriptionService aiAudioTranscriptionService,
            AudioLifecyclePipelineService pipeline,
            ConfigurationService configService,
            IAudioLibraryRepository audioRepo)
        {
            _aiInsightService = aiInsightService;
            _azureTokenProviderStore = azureTokenProviderStore;
            _modelRuntimeResolver = modelRuntimeResolver;
            _speechResourceRuntimeResolver = speechResourceRuntimeResolver;
            _aiAudioTranscriptionService = aiAudioTranscriptionService;
            _pipeline = pipeline;
            _configService = configService;
            _audioRepo = audioRepo;
        }

        /// <summary>根据阶段分派到对应的处理方法。</summary>
        public async Task ExecuteStageAsync(string audioItemId, AudioLifecycleStage stage, CancellationToken ct, Action<string>? reportProgress = null)
        {
            // 提供空操作的默认值，避免每处都判断 null
            reportProgress ??= _ => { };

            switch (stage)
            {
                case AudioLifecycleStage.Transcribed:
                    await ExecuteTranscribeAsync(audioItemId, ct, reportProgress);
                    break;
                case AudioLifecycleStage.Summarized:
                    await ExecuteSummarizeAsync(audioItemId, ct, reportProgress);
                    break;
                case AudioLifecycleStage.MindMap:
                    await ExecuteMindMapAsync(audioItemId, ct, reportProgress);
                    break;
                case AudioLifecycleStage.Insight:
                    await ExecuteInsightAsync(audioItemId, ct, reportProgress);
                    break;
                case AudioLifecycleStage.PodcastScript:
                    await ExecutePodcastScriptAsync(audioItemId, ct, reportProgress);
                    break;
                case AudioLifecycleStage.Research:
                    await ExecuteResearchAsync(audioItemId, ct, reportProgress);
                    break;
                default:
                    throw new NotSupportedException($"阶段 {stage} 暂不支持队列化执行。");
            }
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

        private async Task ExecuteSummarizeAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"总结未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            const string systemPrompt = "你是一个专业的音频内容分析助手。请根据转录文本生成简洁的 Markdown 总结。\n\n## 概要\n用一段话概括核心内容（50-100字），标注关键时间范围。\n\n## 关键要点\n提炼 3-5 个最重要的发现或观点，每条一句话，标注 [MM:SS]。\n\n## 行动建议\n如果有值得跟进的建议或结论，列出 2-3 条。\n\n## 关键词\n提取 3-5 个关键词。\n\n注意：时间戳格式统一用 [MM:SS]，内容要简洁，不要重复。";
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成总结中（等待完整返回）...");
            var result = await aiService.ChatAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Summary,
                enableReasoning: runtimeRequest.SummaryEnableReasoning);

            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("总结生成为空。");

            reportProgress("保存总结结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Summarized, result);
        }

        // ── 思维导图 ──────────────────────────────────────────

        private async Task ExecuteMindMapAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out _))
                return; // 思维导图失败不阻塞

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            const string systemPrompt = "你是一个结构化分析专家。根据音频转录文本生成思维导图的 JSON 结构。\n你必须严格输出有效 JSON，不要输出任何其他内容（不要 markdown 代码块标记）。\n格式：\n{\"label\":\"主题\",\"children\":[{\"label\":\"分支1\",\"children\":[{\"label\":\"要点1\"},{\"label\":\"要点2\"}]}]}\n层级不超过 3 层，每个分支不超过 5 个子节点。";
            var userContent = $"请根据以下转录内容生成思维导图结构：\n\n{transcript}";

            reportProgress("AI 生成思维导图中（等待完整返回）...");
            var json = await aiService.ChatAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

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
        }

        // ── 顿悟 ──────────────────────────────────────────────

        private async Task ExecuteInsightAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"顿悟未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            const string systemPrompt = "你是一个深度思考专家。根据音频转录内容，提供深层洞察和顿悟。\n请从以下角度分析：\n1. **隐含假设**：说话者可能不自知的假设\n2. **潜在矛盾**：观点之间的冲突\n3. **深层模式**：反复出现的主题或思维模式\n4. **未说出的内容**：重要但被忽略的方面\n5. **关联启发**：与其他领域的联系\n\n以 Markdown 格式输出，标注时间戳 [HH:MM:SS]。";
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成顿悟分析中（等待完整返回）...");
            var result = await aiService.ChatAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

            reportProgress("保存顿悟分析结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Insight, result);
        }

        // ── 播客台本 ──────────────────────────────────────────

        private async Task ExecutePodcastScriptAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
        {
            reportProgress("加载转录数据...");
            var transcript = LoadTranscriptTextOrThrow(audioItemId);

            reportProgress("加载 AI 配置...");
            var config = await _configService.LoadConfigAsync();

            if (!TryBuildTextRuntimeConfig(config, out var runtimeRequest, out var endpoint, out var errorMessage))
                throw new InvalidOperationException($"播客未启动：{errorMessage}");

            reportProgress("认证 AI 服务...");
            var aiService = await CreateAuthenticatedInsightServiceAsync(runtimeRequest, endpoint, ct);

            const string systemPrompt = "你是一个播客脚本编写专家。根据音频转录内容，生成一段适合播客的对话内容改写。\n\n严格使用以下格式，每行一句：\n发言人 A：[主持人台词]\n发言人 B：[嘉宾台词]\n\n要求：\n1. 对话总轮次控制在 40 轮以内（A 和 B 各约 20 轮）\n2. 每轮发言控制在 200 字以内\n3. 口语化、自然过渡\n4. 不要加 Markdown 格式、括号注释或舞台指导\n5. 第一行必须是发言人 A 的开场白\n6. 突出有趣的细节和故事";
            var userContent = $"以下是音频转录内容：\n\n{transcript}";

            reportProgress("AI 生成播客台本中（等待完整返回）...");
            var result = await aiService.ChatAsync(
                runtimeRequest, systemPrompt, userContent,
                ct, AiChatProfile.Quick);

            reportProgress("保存播客台本结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.PodcastScript, result);
        }

        // ── 深度研究 ──────────────────────────────────────────

        private async Task ExecuteResearchAsync(string audioItemId, CancellationToken ct, Action<string> reportProgress)
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
            var planResult = await aiService.ChatAsync(
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
            var result = await aiService.ChatAsync(
                runtimeRequest,
                "你是一个深度研究分析师。用户提供了音频转录内容和研究课题列表。请针对每个课题展开深度分析，包括核心论点和支撑证据、不同视角和反驳、与现有知识体系的关联、进一步研究建议。以 Markdown 格式输出，使用标题分隔各课题。引用时标注 [HH:MM:SS]。",
                $"研究课题：\n{selectedTopics}\n\n音频转录内容：\n{transcript}",
                ct, AiChatProfile.Summary,
                enableReasoning: runtimeRequest.SummaryEnableReasoning);

            reportProgress("保存研究报告结果...");
            _pipeline.SaveStageContent(audioItemId, AudioLifecycleStage.Research, result);
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
