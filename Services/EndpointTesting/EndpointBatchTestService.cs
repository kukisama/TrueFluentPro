using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointTesting;

public sealed class EndpointBatchTestService : IEndpointBatchTestService
{
    private readonly IAzureTokenProviderStore _azureTokenProviderStore;
    private readonly IAiAudioTranscriptionService _aiAudioTranscriptionService;
    private readonly IRealtimeConnectionSpecResolver _realtimeConnectionSpecResolver;

    private static readonly ModelCapability[] CapabilityOrder =
    [
        ModelCapability.Text,
        ModelCapability.Image,
        ModelCapability.SpeechToText,
        ModelCapability.TextToSpeech,
        ModelCapability.Video
    ];

    private const int MaxConcurrency = 6;

    public EndpointBatchTestService(
        IAzureTokenProviderStore azureTokenProviderStore,
        IAiAudioTranscriptionService aiAudioTranscriptionService,
        IRealtimeConnectionSpecResolver realtimeConnectionSpecResolver)
    {
        _azureTokenProviderStore = azureTokenProviderStore;
        _aiAudioTranscriptionService = aiAudioTranscriptionService;
        _realtimeConnectionSpecResolver = realtimeConnectionSpecResolver;
    }

    public async Task<EndpointBatchTestReport> TestSelectedEndpointAsync(
        AzureSpeechConfig config,
        AiEndpoint endpoint,
        IProgress<EndpointBatchTestProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(endpoint);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var immediateItems = new List<EndpointBatchTestItem>();
        var progressItems = new List<EndpointBatchTestProgressItem>();
        var pendingTasks = new List<Task<EndpointBatchTestItem[]>>();
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var order = 0;

        if (!config.Endpoints.Any(item => ReferenceEquals(item, endpoint) || item.Id == endpoint.Id))
        {
            var missingItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "整体", "", "当前选中的终结点已不在配置列表中，未执行测试。", "请先保存或重新选择一个仍存在的终结点后再测试。");
            immediateItems.Add(missingItem);
            progressItems.Add(ToProgressItem(missingItem));
            ReportProgress(progress, startedAt, endpoint, progressItems, false);
        }
        else if (!endpoint.IsEnabled)
        {
            var disabledItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "整体", "", "当前选中的终结点已停用，未执行测试。", "如需验证连通性，请先启用该终结点后再运行测试。");
            immediateItems.Add(disabledItem);
            progressItems.Add(ToProgressItem(disabledItem));
            ReportProgress(progress, startedAt, endpoint, progressItems, false);
        }
        else
        {
            var aadPrecheckFailure = await BuildAadPrecheckFailureItemAsync(config, endpoint, order, cancellationToken);
            if (aadPrecheckFailure != null)
            {
                immediateItems.Add(aadPrecheckFailure);
                progressItems.Add(ToProgressItem(aadPrecheckFailure));
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
            }

            var models = endpoint.Models?.ToList() ?? new List<AiModelEntry>();
            if (aadPrecheckFailure == null && models.Count == 0)
            {
                var noModelItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "整体", "", "当前选中的终结点未配置任何模型。", "请先为这个终结点添加至少一个文字、图片或视频模型。");
                immediateItems.Add(noModelItem);
                progressItems.Add(ToProgressItem(noModelItem));
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
            }

            foreach (var model in aadPrecheckFailure == null ? models : Enumerable.Empty<AiModelEntry>())
            {
                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    var skippedItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, GetCapabilityNameOrFallback(model.Capabilities), "", "模型 ID 未填写，未执行测试。", "本次只测试当前选中终结点里已经明确写好的模型；空白模型行会直接跳过。");
                    immediateItems.Add(skippedItem);
                    progressItems.Add(ToProgressItem(skippedItem));
                    continue;
                }

                var effectiveCapabilities = EndpointCapabilityPolicyResolver.ApplyCapabilityPolicy(endpoint.ProfileId, endpoint.EndpointType, model.Capabilities);
                var capabilities = CapabilityOrder.Where(capability => effectiveCapabilities.HasFlag(capability)).ToList();
                if (capabilities.Count == 0)
                {
                    var unsupportedItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "未知能力", model.ModelId, "模型能力未标记为文字、图片、视频或语音转写，未执行测试。", "请先为该模型选择正确的能力类型，再重新发起测试。");
                    immediateItems.Add(unsupportedItem);
                    progressItems.Add(ToProgressItem(unsupportedItem));
                    continue;
                }

                foreach (var capability in capabilities)
                {
                    if (capability == ModelCapability.TextToSpeech)
                    {
                        var skippedTtsItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, GetCapabilityName(capability), model.ModelId, "文字转语音暂未提供一键快速测试。", "当前一键测试已覆盖文字、图片、视频和语音转写；TTS 快速探活尚未接入，先跳过该模型。" );
                        immediateItems.Add(skippedTtsItem);
                        progressItems.Add(ToProgressItem(skippedTtsItem));
                        continue;
                    }

                    var plans = BuildCapabilityTestPlans(endpoint, model, capability, config.MediaGenConfig);
                    if (plans.Count == 0)
                    {
                        var failedItem = CreateProfileMissingItem(order++, endpoint, model, capability, config.MediaGenConfig);
                        immediateItems.Add(failedItem);
                        progressItems.Add(ToProgressItem(failedItem));
                        continue;
                    }

                    var planInfos = plans.Select(plan =>
                    {
                        var o = order++;
                        var pi = CreatePendingProgressItem(o, endpoint, model, plan);
                        progressItems.Add(pi);
                        return (Plan: plan, Order: o, Pending: pi);
                    }).ToList();

                    pendingTasks.Add(RunPrimaryThenFallbackAsync(gate, endpoint, model, planInfos, config, progressItems, progress, startedAt, cancellationToken));
                }
            }

            if (progressItems.Count > 0)
            {
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
            }
        }

        var completedGroups = pendingTasks.Count == 0 ? Array.Empty<EndpointBatchTestItem[]>() : await Task.WhenAll(pendingTasks);
        var completedItems = completedGroups.SelectMany(g => g);
        stopwatch.Stop();

        var report = new EndpointBatchTestReport
        {
            StartedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Items = immediateItems.Concat(completedItems).OrderBy(item => item.Order).ToList()
        };

        var finalProgressItems = report.Items.Select(ToProgressItem).OrderBy(item => item.Order).ToList();
        ReportProgress(progress, startedAt, endpoint, finalProgressItems, true);
        return report;
    }

    private async Task<EndpointBatchTestItem?> BuildAadPrecheckFailureItemAsync(
        AzureSpeechConfig config,
        AiEndpoint endpoint,
        int order,
        CancellationToken cancellationToken)
    {
        if (endpoint.AuthMode != AzureAuthMode.AAD)
        {
            return null;
        }

        if (!endpoint.IsAzureEndpoint)
        {
            return CreateFailedItem(
                order,
                endpoint.Id,
                GetEndpointName(endpoint),
                endpoint.EndpointTypeDisplayName,
                "整体",
                string.Empty,
                string.Empty,
                "AAD 预检",
                TimeSpan.Zero,
                "AAD 测试未执行。",
                "当前只有 Azure OpenAI 终结点支持 AAD 测试，请确认终结点类型与资料包声明一致。");
        }

        if (string.IsNullOrWhiteSpace(endpoint.AzureTenantId))
        {
            return CreateFailedItem(
                order,
                endpoint.Id,
                GetEndpointName(endpoint),
                endpoint.EndpointTypeDisplayName,
                "整体",
                string.Empty,
                string.Empty,
                "AAD 预检",
                TimeSpan.Zero,
                "AAD 测试未执行。",
                "当前终结点未填写 Tenant ID，无法按抽象后的 endpoint profile 恢复登录状态。");
        }

        var profileKey = $"endpoint_{endpoint.Id}";
        var provider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
            profileKey,
            endpoint.AzureTenantId,
            endpoint.AzureClientId,
            cancellationToken);

        return provider == null
            ? CreateFailedItem(
                order,
                endpoint.Id,
                GetEndpointName(endpoint),
                endpoint.EndpointTypeDisplayName,
                "整体",
                string.Empty,
                string.Empty,
                $"AAD 预检\nProfileKey：{profileKey}",
                TimeSpan.Zero,
                "AAD 测试未执行。",
                "当前测试严格使用该终结点自己的 endpoint profile 登录态，不再回退到其它共享 key。请先在这个终结点卡片内完成 AAD 登录，再重新测试。")
            : null;
    }

    private static async Task<EndpointBatchTestItem> RunWithGateAsync(SemaphoreSlim gate, Func<Task<EndpointBatchTestItem>> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private async Task<EndpointBatchTestItem[]> RunPrimaryThenFallbackAsync(
        SemaphoreSlim gate,
        AiEndpoint endpoint,
        AiModelEntry model,
        List<(CapabilityTestPlan Plan, int Order, EndpointBatchTestProgressItem Pending)> planInfos,
        AzureSpeechConfig config,
        List<EndpointBatchTestProgressItem> progressItems,
        IProgress<EndpointBatchTestProgressSnapshot>? progress,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var primary = planInfos[0];

        var primaryResult = await RunWithGateAsync(gate, async () =>
        {
            ReplaceProgressItem(progressItems, CreateRunningProgressItem(primary.Pending));
            ReportProgress(progress, startedAt, endpoint, progressItems, false);
            var result = await TestCapabilityAsync(primary.Order, endpoint, model, primary.Plan, config, cancellationToken);
            ReplaceProgressItem(progressItems, ToProgressItem(result));
            ReportProgress(progress, startedAt, endpoint, progressItems, false);
            return result;
        }, cancellationToken);

        var results = new List<EndpointBatchTestItem> { primaryResult };

        if (planInfos.Count == 1)
            return results.ToArray();

        if (primaryResult.Status == EndpointBatchTestStatus.Success)
        {
            foreach (var fb in planInfos.Skip(1))
            {
                var skipped = CreateSkippedItem(fb.Order, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName,
                    fb.Plan.CapabilityDisplayName, model.ModelId,
                    "主地址测试通过，回退地址已跳过。",
                    $"因主地址（{primary.Plan.Url}）测试成功，本回退分支未执行实际测试。");
                results.Add(skipped);
                ReplaceProgressItem(progressItems, ToProgressItem(skipped));
            }
            ReportProgress(progress, startedAt, endpoint, progressItems, false);
        }
        else
        {
            var fallbackTasks = planInfos.Skip(1).Select(fb => RunWithGateAsync(gate, async () =>
            {
                ReplaceProgressItem(progressItems, CreateRunningProgressItem(fb.Pending));
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
                var result = await TestCapabilityAsync(fb.Order, endpoint, model, fb.Plan, config, cancellationToken);
                ReplaceProgressItem(progressItems, ToProgressItem(result));
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
                return result;
            }, cancellationToken)).ToArray();

            results.AddRange(await Task.WhenAll(fallbackTasks));
        }

        return results.ToArray();
    }

    private async Task<EndpointBatchTestItem> TestCapabilityAsync(int order, AiEndpoint endpoint, AiModelEntry model, CapabilityTestPlan plan, AzureSpeechConfig config, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpointName = GetEndpointName(endpoint);
        var endpointTypeName = endpoint.EndpointTypeDisplayName;

        try
        {
            ValidateEndpoint(endpoint, model, plan.Capability);

            var runtime = new ModelRuntimeResolution
            {
                Endpoint = endpoint,
                Model = model,
                Capability = plan.Capability
            };

            return plan.Capability switch
            {
                ModelCapability.Text => await TestTextAsync(order, runtime, plan, endpointName, endpointTypeName, stopwatch, cancellationToken),
                ModelCapability.Image => await TestImageAsync(order, runtime, plan, endpointName, endpointTypeName, config.MediaGenConfig, stopwatch, cancellationToken),
                ModelCapability.SpeechToText => ShouldUseRealtimeSpeechProbe(runtime)
                    ? await TestRealtimeSpeechAsync(order, runtime, plan, endpointName, endpointTypeName, stopwatch, cancellationToken)
                    : await TestAudioAsync(order, runtime, plan, endpointName, endpointTypeName, stopwatch, cancellationToken),
                ModelCapability.Video => await TestVideoAsync(order, runtime, plan, endpointName, endpointTypeName, config.MediaGenConfig, stopwatch, cancellationToken),
                _ => CreateFailedItem(order, endpoint.Id, endpointName, endpointTypeName, plan.CapabilityDisplayName, model.ModelId, plan.RequestUrlText, plan.RequestSummary, stopwatch.Elapsed, "暂不支持该测试类型。", "当前仅支持文字、图片、视频三类连通性测试。")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return CreateFailedItem(order, endpoint.Id, endpointName, endpointTypeName, plan.CapabilityDisplayName, model.ModelId, plan.RequestUrlText, plan.RequestSummary, stopwatch.Elapsed, $"{plan.CapabilityDisplayName}测试超时。", "请求已达到等待上限，请检查终结点地址、模型部署名、网络连通性、权限配置或厂商资料包是否声明完整。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CreateFailedItem(order, endpoint.Id, endpointName, endpointTypeName, plan.CapabilityDisplayName, model.ModelId, plan.RequestUrlText, plan.RequestSummary, stopwatch.Elapsed, $"{plan.CapabilityDisplayName}测试失败。", FormatExceptionDetails(ex));
        }
    }

    private async Task<EndpointBatchTestItem> TestTextAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiInsightService(tokenProvider);
        var request = runtime.CreateChatRequest(summaryEnableReasoning: false);
        var responseBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        AiRequestTrace? requestTrace = null;

        await service.StreamChatAsync(
            request,
            "你是连通性测试助手，请用简短中文直接回复。",
            "计算 2+3 的结果",
            chunk => { if (responseBuilder.Length < 160) responseBuilder.Append(chunk); },
            timeoutCts.Token,
            AiChatProfile.Quick,
            enableReasoning: true,
            onTrace: trace => requestTrace = trace,
            onReasoningChunk: chunk => { if (reasoningBuilder.Length < 200) reasoningBuilder.Append(chunk); },
            urlCandidatesOverride: new[] { plan.Url },
            allowNextUrlRetry: false,
            allowApimSubscriptionKeyQueryRetry: false);

        stopwatch.Stop();
        var requestUrlText = BuildTextRequestUrlText(plan.Url, requestTrace);
        var responseText = responseBuilder.ToString().Trim();
        var reasoningText = reasoningBuilder.ToString().Trim();
        var reasoningStatus = reasoningText.Length > 0
            ? "✅ 推理可用"
            : "⚠️ 推理未返回（模型可能不支持 reasoning）";

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return CreateFailedItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, "文字测试返回为空。", $"请求已成功发出，但没有收到有效文本响应；请检查模型是否支持当前资料包声明的文本接口。\n\n原始返回片段为空。\n\n{reasoningStatus}");
        }

        return CreateSuccessItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, $"文字测试通过。{reasoningStatus}", $"返回片段：{responseText}{(reasoningText.Length > 0 ? $"\n\n推理片段：{reasoningText}" : "")}");
    }

    private async Task<EndpointBatchTestItem> TestImageAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, MediaGenConfig sourceConfig, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiImageGenService();
        service.SetTokenProvider(tokenProvider);

        var requestConfig = runtime.CreateRequestConfig();
        var genConfig = CreateImageTestConfig(sourceConfig, runtime.ModelId);
        var routeResult = await service.ProbeGenerateCandidateUrlsAsync(requestConfig, "请生成一只卡通兔子。", genConfig, plan.RouteLabel, new[] { plan.Url }, timeoutCts.Token);

        stopwatch.Stop();
        var requestUrlText = BuildImageProbeRequestUrlText(plan.Url, routeResult);

        if (routeResult.IsSuccess && routeResult.ImageCount > 0)
        {
            return CreateSuccessItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, $"图片测试通过，已返回 {routeResult.ImageCount} 张图片。", BuildImageProbeDetails(routeResult));
        }

        return CreateFailedItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, "图片测试失败。", BuildImageProbeDetails(routeResult));
    }

    private async Task<EndpointBatchTestItem> TestAudioAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        var tempAudioPath = CreateAudioProbeFile();
        try
        {
            var result = await _aiAudioTranscriptionService.ProbeAsync(runtime, tempAudioPath, "zh-CN", timeoutCts.Token);
            stopwatch.Stop();
            var requestUrlText = BuildAudioRequestUrlText(plan.Url, result.FinalUrl);
            var details = BuildAudioProbeDetails(result);

            return CreateSuccessItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, BuildAudioProbeSummary(result), details);
        }
        finally
        {
            TryDeleteFile(tempAudioPath);
        }
    }

    private async Task<EndpointBatchTestItem> TestRealtimeSpeechAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        if (!_realtimeConnectionSpecResolver.TryResolve(runtime, out var spec, out var errorMessage) || spec == null)
        {
            stopwatch.Stop();
            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                plan.CapabilityDisplayName,
                runtime.ModelId,
                plan.RequestUrlText,
                plan.RequestSummary,
                stopwatch.Elapsed,
                "Realtime 语音模型测试失败。",
                errorMessage);
        }

        using var socket = new ClientWebSocket();
        await ApplyRealtimeAuthenticationAsync(socket, runtime, spec, timeoutCts.Token);
        await socket.ConnectAsync(spec.WebSocketUri, timeoutCts.Token);

        await SendRealtimeProbeSessionUpdateAsync(socket, spec, timeoutCts.Token);
        await SendRealtimeProbeConversationAsync(socket, spec, timeoutCts.Token);

        var probeResult = await ReceiveRealtimeProbeResultAsync(socket, timeoutCts.Token);
        stopwatch.Stop();

        if (!probeResult.IsSuccess)
        {
            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                plan.CapabilityDisplayName,
                runtime.ModelId,
                $"WS {spec.WebSocketUri}",
                plan.RequestSummary + $"\n测试说明：当前模型识别为 Realtime 语音模型，已改走官方 /realtime WebSocket 快速探活。",
                stopwatch.Elapsed,
                "Realtime 语音模型测试失败。",
                probeResult.Details);
        }

        return CreateSuccessItem(
            order,
            runtime.EndpointId,
            endpointName,
            endpointTypeName,
            plan.CapabilityDisplayName,
            runtime.ModelId,
            $"WS {spec.WebSocketUri}",
            plan.RequestSummary + $"\n测试说明：当前模型识别为 Realtime 语音模型，已改走官方 /realtime WebSocket 快速探活。",
            stopwatch.Elapsed,
            BuildRealtimeProbeSummary(probeResult.ResponsePreview),
            BuildRealtimeProbeDetails(spec, probeResult));
    }

    private async Task<EndpointBatchTestItem> TestVideoAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, MediaGenConfig sourceConfig, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(45));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiVideoGenService();
        service.SetTokenProvider(tokenProvider);

        var requestConfig = runtime.CreateRequestConfig();
        var genConfig = CreateVideoTestConfig(sourceConfig, runtime.ModelId);
        genConfig.VideoApiMode = EndpointProfileVideoModeResolver.ResolveVideoApiMode(runtime.ProfileId, runtime.EndpointType, runtime.ModelId, genConfig.VideoApiMode);
        var videoId = await service.CreateVideoAsync(requestConfig, "请生成一只卡通兔子。", genConfig, null, timeoutCts.Token, new[] { plan.Url }, allowFallbacks: false, allowApimSubscriptionKeyQueryFallback: false);

        stopwatch.Stop();
        var requestUrlText = BuildVideoCreateRequestUrlText(service, plan.Url);

        return CreateSuccessItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, "视频创建测试通过。", $"video_id：{videoId}\n说明：当前一键测试只验证视频创建接口，不再执行轮询和下载。\n分支：{plan.RouteLabel}");
    }

    private static void ValidateEndpoint(AiEndpoint endpoint, AiModelEntry model, ModelCapability capability)
    {
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl)) throw new InvalidOperationException("未填写 API 地址。");
        if (endpoint.AuthMode == AzureAuthMode.AAD)
        {
            if (!endpoint.IsAzureEndpoint) throw new InvalidOperationException("当前仅 Azure OpenAI 终结点支持 AAD 测试。");
            if (string.IsNullOrWhiteSpace(endpoint.AzureTenantId)) throw new InvalidOperationException("AAD 测试缺少 Tenant ID。");
        }
        else if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            throw new InvalidOperationException("未填写 API 密钥。");
        }

        if (string.IsNullOrWhiteSpace(model.ModelId)) throw new InvalidOperationException($"{GetCapabilityName(capability)}模型的模型 ID 为空。");
    }

    private async Task<AzureTokenProvider?> CreateTokenProviderAsync(ModelRuntimeResolution runtime, CancellationToken cancellationToken)
    {
        if (runtime.AzureAuthMode != AzureAuthMode.AAD) return null;
        var tokenProvider = await _azureTokenProviderStore.GetAuthenticatedProviderAsync(
            runtime.ProfileKey,
            runtime.AzureTenantId,
            runtime.AzureClientId,
            cancellationToken);
        if (tokenProvider == null) throw new InvalidOperationException("AAD 未登录，请先在当前终结点里完成登录后再测试。");
        return tokenProvider;
    }

    private static MediaGenConfig CreateImageTestConfig(MediaGenConfig source, string modelId) => new()
    {
        ImageModel = modelId,
        ImageSize = string.IsNullOrWhiteSpace(source.ImageSize) ? "1024x1024" : source.ImageSize,
        ImageQuality = string.IsNullOrWhiteSpace(source.ImageQuality) ? "medium" : source.ImageQuality,
        ImageFormat = string.IsNullOrWhiteSpace(source.ImageFormat) ? "png" : source.ImageFormat,
        ImageCount = 1
    };

    private static MediaGenConfig CreateVideoTestConfig(MediaGenConfig source, string modelId) => new()
    {
        VideoModel = modelId,
        VideoApiMode = source.VideoApiMode,
        VideoWidth = source.VideoWidth <= 0 ? 1280 : source.VideoWidth,
        VideoHeight = source.VideoHeight <= 0 ? 720 : source.VideoHeight,
        VideoAspectRatio = string.IsNullOrWhiteSpace(source.VideoAspectRatio) ? "16:9" : source.VideoAspectRatio,
        VideoResolution = string.IsNullOrWhiteSpace(source.VideoResolution) ? "720p" : source.VideoResolution,
        VideoSeconds = Math.Clamp(source.VideoSeconds <= 0 ? 4 : source.VideoSeconds, 1, 4),
        VideoVariants = 1,
        VideoPollIntervalMs = Math.Clamp(source.VideoPollIntervalMs <= 0 ? 3000 : source.VideoPollIntervalMs, 500, 60000)
    };

    private static EndpointBatchTestItem CreateSuccessItem(int order, string endpointId, string endpointName, string endpointTypeName, string capabilityName, string modelId, string requestUrlText, string requestSummary, TimeSpan duration, string summary, string details) => new()
    {
        Order = order,
        EndpointId = endpointId,
        EndpointName = endpointName,
        EndpointTypeName = endpointTypeName,
        CapabilityName = capabilityName,
        ModelId = modelId,
        Status = EndpointBatchTestStatus.Success,
        Summary = summary,
        Details = details,
        RequestUrlText = requestUrlText,
        RequestSummary = requestSummary,
        Duration = duration
    };

    private static EndpointBatchTestItem CreateFailedItem(int order, string endpointId, string endpointName, string endpointTypeName, string capabilityName, string modelId, string requestUrlText, string requestSummary, TimeSpan duration, string summary, string details) => new()
    {
        Order = order,
        EndpointId = endpointId,
        EndpointName = endpointName,
        EndpointTypeName = endpointTypeName,
        CapabilityName = capabilityName,
        ModelId = modelId,
        Status = EndpointBatchTestStatus.Failed,
        Summary = summary,
        Details = details,
        RequestUrlText = requestUrlText,
        RequestSummary = requestSummary,
        Duration = duration
    };

    private static EndpointBatchTestItem CreateSkippedItem(int order, string endpointName, string endpointTypeName, string summary, string details)
        => CreateSkippedItem(order, "", endpointName, endpointTypeName, "整体", "", summary, details);

    private static EndpointBatchTestItem CreateSkippedItem(int order, string endpointId, string endpointName, string endpointTypeName, string capabilityName, string modelId, string summary, string details) => new()
    {
        Order = order,
        EndpointId = endpointId,
        EndpointName = endpointName,
        EndpointTypeName = endpointTypeName,
        CapabilityName = capabilityName,
        ModelId = modelId,
        Status = EndpointBatchTestStatus.Skipped,
        Summary = summary,
        Details = details,
        RequestUrlText = "",
        RequestSummary = "",
        Duration = TimeSpan.Zero
    };

    private static EndpointBatchTestItem CreateProfileMissingItem(int order, AiEndpoint endpoint, AiModelEntry model, ModelCapability capability, MediaGenConfig mediaGenConfig)
    {
        var capabilityName = capability == ModelCapability.Video ? "视频（创建）" : GetCapabilityName(capability);
        return CreateFailedItem(order, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, capabilityName, model.ModelId, string.Empty, BuildRequestSummary(endpoint, model, capability, mediaGenConfig), TimeSpan.Zero, $"{capabilityName}测试未执行：资料包未声明候选 URL。", "当前一键测试已改为严格按资料包执行；如果资料包没有给出该能力的 URL 候选，本工具不会再自动拼装兜底路线。请让厂商补齐资料包后再测。");
    }

    private static EndpointBatchTestProgressItem CreatePendingProgressItem(int order, AiEndpoint endpoint, AiModelEntry model, CapabilityTestPlan plan) => new()
    {
        Order = order,
        EndpointId = endpoint.Id,
        EndpointName = GetEndpointName(endpoint),
        EndpointTypeName = endpoint.EndpointTypeDisplayName,
        CapabilityName = plan.CapabilityDisplayName,
        ModelId = model.ModelId,
        State = EndpointBatchTestLiveState.Pending,
        Summary = "等待测试开始。",
        Details = $"已加入当前终结点的测试队列。\n分支：{plan.RouteLabel}",
        RequestUrlText = plan.RequestUrlText,
        RequestSummary = plan.RequestSummary,
        Duration = TimeSpan.Zero
    };

    private static EndpointBatchTestProgressItem CreateRunningProgressItem(EndpointBatchTestProgressItem source) => new()
    {
        Order = source.Order,
        EndpointId = source.EndpointId,
        EndpointName = source.EndpointName,
        EndpointTypeName = source.EndpointTypeName,
        CapabilityName = source.CapabilityName,
        ModelId = source.ModelId,
        State = EndpointBatchTestLiveState.Running,
        Summary = "正在测试...",
        Details = "请求已发出，正在等待终结点返回结果。",
        RequestUrlText = source.RequestUrlText,
        RequestSummary = source.RequestSummary,
        Duration = TimeSpan.Zero
    };

    private static EndpointBatchTestProgressItem ToProgressItem(EndpointBatchTestItem item) => new()
    {
        Order = item.Order,
        EndpointId = item.EndpointId,
        EndpointName = item.EndpointName,
        EndpointTypeName = item.EndpointTypeName,
        CapabilityName = item.CapabilityName,
        ModelId = item.ModelId,
        State = item.Status switch
        {
            EndpointBatchTestStatus.Success => EndpointBatchTestLiveState.Success,
            EndpointBatchTestStatus.Failed => EndpointBatchTestLiveState.Failed,
            _ => EndpointBatchTestLiveState.Skipped
        },
        Summary = item.Summary,
        Details = item.Details,
        RequestUrlText = item.RequestUrlText,
        RequestSummary = item.RequestSummary,
        Duration = item.Duration
    };

    private static void ReplaceProgressItem(List<EndpointBatchTestProgressItem> items, EndpointBatchTestProgressItem item)
    {
        var index = items.FindIndex(existing => existing.Order == item.Order);
        if (index >= 0) items[index] = item; else items.Add(item);
    }

    private static void ReportProgress(IProgress<EndpointBatchTestProgressSnapshot>? progress, DateTimeOffset startedAt, AiEndpoint endpoint, IReadOnlyList<EndpointBatchTestProgressItem> items, bool isCompleted)
    {
        progress?.Report(new EndpointBatchTestProgressSnapshot
        {
            StartedAt = startedAt,
            CompletedAt = isCompleted ? DateTimeOffset.Now : null,
            EndpointId = endpoint.Id,
            EndpointName = GetEndpointName(endpoint),
            IsCompleted = isCompleted,
            Items = items.OrderBy(item => item.Order).ToList()
        });
    }

    private static string GetEndpointName(AiEndpoint endpoint) => string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Id : endpoint.Name;

    private static string GetCapabilityNameOrFallback(ModelCapability capability)
    {
        foreach (var candidate in CapabilityOrder)
        {
            if (capability.HasFlag(candidate)) return GetCapabilityName(candidate);
        }
        return "未知能力";
    }

    private static string GetCapabilityName(ModelCapability capability) => capability switch
    {
        ModelCapability.Text => "文字",
        ModelCapability.Image => "图片",
        ModelCapability.SpeechToText => "语音转写",
        ModelCapability.TextToSpeech => "文字转语音",
        ModelCapability.Video => "视频",
        _ => capability.ToString()
    };

    private static IReadOnlyList<CapabilityTestPlan> BuildCapabilityTestPlans(AiEndpoint endpoint, AiModelEntry model, ModelCapability capability, MediaGenConfig mediaGenConfig)
    {
        var runtime = new ModelRuntimeResolution { Endpoint = endpoint, Model = model, Capability = capability };
        var baseSummary = BuildRequestSummary(endpoint, model, capability, mediaGenConfig);

        return capability switch
        {
            ModelCapability.Text => BuildRoutePlans(capability, GetCapabilityName(capability), BuildStrictTextUrls(runtime), baseSummary, "POST"),
            ModelCapability.Image => BuildRoutePlans(capability, GetCapabilityName(capability), BuildStrictImageUrls(runtime), baseSummary, "POST"),
            ModelCapability.SpeechToText => ShouldUseRealtimeSpeechProbe(runtime)
                ? BuildRealtimeRoutePlans(capability, runtime, baseSummary)
                : BuildRoutePlans(capability, GetCapabilityName(capability), BuildStrictAudioUrls(runtime), baseSummary + "\n测试说明：使用内置短音频做快速探活，只验证上传 / 鉴权 / 路由是否打通，不保证返回有意义文本。", "POST"),
            ModelCapability.Video => BuildRoutePlans(capability, "视频（创建）", BuildStrictVideoCreateUrls(runtime, mediaGenConfig), baseSummary + "\n视频测试：仅验证创建接口，不再执行轮询和下载。", "POST"),
            _ => Array.Empty<CapabilityTestPlan>()
        };
    }

    private static IReadOnlyList<CapabilityTestPlan> BuildRealtimeRoutePlans(ModelCapability capability, ModelRuntimeResolution runtime, string baseSummary)
    {
        var uriText = runtime.IsAzureEndpoint
            ? "WS /realtime（按官方 Azure OpenAI Realtime 协议解析）"
            : "WS /realtime（按官方 OpenAI Realtime 协议解析）";

        return
        [
            new CapabilityTestPlan(
                capability,
                GetCapabilityName(capability),
                uriText,
                "Realtime WebSocket 快速探活",
                uriText,
                baseSummary + "\n测试说明：当前模型名命中 realtime 族，快速测试会改为建立官方 Realtime WebSocket 会话，并发送一条极短文本消息验证会话与响应链路。")
        ];
    }

    private static IReadOnlyList<CapabilityTestPlan> BuildRoutePlans(ModelCapability capability, string primaryCapabilityName, IReadOnlyList<string> urls, string baseSummary, string method)
    {
        var plans = new List<CapabilityTestPlan>();
        for (var i = 0; i < urls.Count; i++)
        {
            var isPrimary = i == 0;
            var routeLabel = isPrimary ? "主测试（资料包第 1 条候选）" : $"回退测试 {i}（资料包第 {i + 1} 条候选）";
            var capabilityName = isPrimary ? primaryCapabilityName : $"{primaryCapabilityName}（回退 {i}）";
            plans.Add(new CapabilityTestPlan(capability, capabilityName, urls[i], routeLabel, $"{method} {urls[i]}", $"{baseSummary}\n测试来源：资料包\n测试分支：{routeLabel}"));
        }
        return plans;
    }

    private static IReadOnlyList<string> BuildStrictTextUrls(ModelRuntimeResolution runtime)
        => EndpointProfileUrlBuilder.BuildConfiguredTextUrlCandidates(runtime.ApiEndpoint, runtime.ProfileId, runtime.EndpointType, runtime.TextApiProtocolMode, runtime.IsAzureEndpoint, runtime.EffectiveDeploymentName, runtime.ApiVersion);

    private static IReadOnlyList<string> BuildStrictImageUrls(ModelRuntimeResolution runtime)
        => EndpointProfileUrlBuilder.BuildConfiguredImageGenerateUrlCandidates(runtime.ApiEndpoint, runtime.ProfileId, runtime.EndpointType, runtime.ImageApiRouteMode, runtime.EffectiveDeploymentName, runtime.ApiVersion);

    private static IReadOnlyList<string> BuildStrictAudioUrls(ModelRuntimeResolution runtime)
        => EndpointProfileUrlBuilder.BuildConfiguredAudioTranscriptionUrlCandidates(runtime.ApiEndpoint, runtime.ProfileId, runtime.EndpointType, runtime.EffectiveDeploymentName, runtime.ApiVersion);

    private static IReadOnlyList<string> BuildStrictVideoCreateUrls(ModelRuntimeResolution runtime, MediaGenConfig mediaGenConfig)
        => EndpointProfileUrlBuilder.BuildConfiguredVideoCreateUrlCandidates(
            runtime.ApiEndpoint,
            runtime.ProfileId,
            runtime.EndpointType,
            runtime.ApiVersion,
            EndpointProfileVideoModeResolver.ResolveVideoApiMode(runtime.ProfileId, runtime.EndpointType, runtime.ModelId, mediaGenConfig.VideoApiMode));

    private static string BuildRequestSummary(AiEndpoint endpoint, AiModelEntry model, ModelCapability capability, MediaGenConfig mediaGenConfig)
    {
        var auth = DescribeAuth(endpoint);
        var deployment = endpoint.IsAzureEndpoint ? $"部署：{(string.IsNullOrWhiteSpace(model.DeploymentName) ? model.ModelId : model.DeploymentName)}" : $"模型：{model.ModelId}";
        var effectiveVideoMode = EndpointProfileVideoModeResolver.ResolveVideoApiMode(endpoint.ProfileId, endpoint.EndpointType, model.ModelId, mediaGenConfig.VideoApiMode);
        var rawApiVersion = string.IsNullOrWhiteSpace(endpoint.ApiVersion) ? "未显式填写" : endpoint.ApiVersion.Trim();
        var isRealtimeSpeech = capability == ModelCapability.SpeechToText && LooksLikeRealtimeSpeechModel(string.IsNullOrWhiteSpace(model.DeploymentName) ? model.ModelId : model.DeploymentName);
        var capabilitySuffix = capability switch
        {
            ModelCapability.Text => $"文本协议：{DescribeTextProtocol(endpoint)}",
            ModelCapability.Image => $"图片路由：{DescribeImageRoute(endpoint)}",
            ModelCapability.SpeechToText => isRealtimeSpeech
                ? $"音频路由：官方 Realtime WebSocket\nRealtime 路线：{DescribeRealtimeRoute(endpoint, model)}"
                : $"音频路由：{DescribeAudioRoute(endpoint)}",
            ModelCapability.Video => BuildVideoModeSummary(mediaGenConfig.VideoApiMode, effectiveVideoMode),
            _ => capability.ToString()
        };
        var versionSummary = isRealtimeSpeech
            ? BuildRealtimeApiVersionSummary(endpoint, model)
            : BuildApiVersionSummary(endpoint, effectiveVideoMode);
        return $"认证：{auth}\n基础地址：{endpoint.BaseUrl?.Trim() ?? "未填写"}\n{deployment}\nAPI版本字段：{rawApiVersion}\n{versionSummary}\n{capabilitySuffix}";
    }

    private static string BuildRealtimeApiVersionSummary(AiEndpoint endpoint, AiModelEntry model)
    {
        var deploymentName = string.IsNullOrWhiteSpace(model.DeploymentName) ? model.ModelId : model.DeploymentName;
        if (IsPreviewRealtimeSpeechModel(model.ModelId) || IsPreviewRealtimeSpeechModel(deploymentName))
        {
            return "版本说明：当前 Realtime Preview 测试固定使用 2025-04-01-preview，不跟随文本接口的 API 版本字段。";
        }

        return endpoint.IsAzureEndpoint
            ? "版本说明：当前 Realtime GA 测试使用 /openai/v1/realtime，不附带 api-version。"
            : "版本说明：当前 OpenAI Realtime GA 测试使用 /v1/realtime，不附带 api-version。";
    }

    private static string BuildApiVersionSummary(AiEndpoint endpoint, VideoApiMode effectiveVideoMode)
    {
        var textVersion = EndpointProfileUrlBuilder.GetEffectiveTextApiVersion(
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            endpoint.IsAzureEndpoint);

        var videoUrls = EndpointProfileUrlBuilder.BuildConfiguredVideoCreateUrlCandidates(
            endpoint.BaseUrl ?? string.Empty,
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiVersion,
            effectiveVideoMode);

        var videoVersionSummary = SummarizeApiVersionCandidates(videoUrls, "视频创建候选");

        return string.IsNullOrWhiteSpace(textVersion)
            ? $"版本说明：当前文本路线不附带 api-version。{Environment.NewLine}{videoVersionSummary}"
            : $"版本说明：当前文本路线会使用 {textVersion}。{Environment.NewLine}{videoVersionSummary}";
    }

    private static string SummarizeApiVersionCandidates(IReadOnlyList<string> urls, string scopeLabel)
    {
        if (urls.Count == 0)
            return $"版本说明：{scopeLabel}未声明。";

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasUnversioned = false;

        foreach (var url in urls)
        {
            var version = TryExtractApiVersion(url);
            if (string.IsNullOrWhiteSpace(version))
                hasUnversioned = true;
            else
                versions.Add(version);
        }

        if (versions.Count == 0)
            return $"版本说明：{scopeLabel}当前都不带 api-version。";

        var versionText = string.Join(" / ", versions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase));
        if (hasUnversioned)
            return $"版本说明：{scopeLabel}当前同时包含无版本 / {versionText}（资料包声明，不完全跟随 API 版本字段）。";

        return $"版本说明：{scopeLabel}当前固定为 {versionText}（资料包声明）。";
    }

    private static string? TryExtractApiVersion(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var marker = "api-version=";
        var index = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var start = index + marker.Length;
        var end = url.IndexOf('&', start);
        return end < 0
            ? url[start..].Trim()
            : url[start..end].Trim();
    }

    private static string BuildVideoModeSummary(VideoApiMode configuredMode, VideoApiMode effectiveMode)
        => configuredMode == effectiveMode
            ? $"视频模式：{effectiveMode}（仅测创建）"
            : $"视频模式：{effectiveMode}（仅测创建，按资料包模型映射解析）";

    private static string BuildTextRequestUrlText(string plannedUrl, AiRequestTrace? trace)
    {
        var lines = new List<string> { $"POST {(string.IsNullOrWhiteSpace(trace?.FinalUrl) ? plannedUrl : trace.FinalUrl)}" };
        if (trace?.AttemptedUrls.Count > 1)
        {
            lines.Add("尝试过的 URL：");
            lines.AddRange(trace.AttemptedUrls.Select(url => $"- {url}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildImageProbeRequestUrlText(string plannedUrl, ImageRouteProbeResult result)
    {
        var lines = new List<string> { $"POST {(string.IsNullOrWhiteSpace(result.SuccessfulUrl) ? plannedUrl : result.SuccessfulUrl)}" };
        if (result.AttemptedUrls.Count > 1)
        {
            lines.Add("尝试过的 URL：");
            lines.AddRange(result.AttemptedUrls.Select(url => $"- {url}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildAudioRequestUrlText(string plannedUrl, string actualUrl)
        => $"POST {(string.IsNullOrWhiteSpace(actualUrl) ? plannedUrl : actualUrl)}";

    private static string BuildVideoCreateRequestUrlText(AiVideoGenService service, string plannedUrl)
    {
        var lines = new List<string> { $"POST {(string.IsNullOrWhiteSpace(service.LastCreateRequestUrl) ? plannedUrl : service.LastCreateRequestUrl)}" };
        if (service.LastCreateAttemptedUrls.Count > 1)
        {
            lines.Add("尝试过的 URL：");
            lines.AddRange(service.LastCreateAttemptedUrls.Select(url => $"- {url}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildImageProbeDetails(ImageRouteProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"分支：{result.RouteLabel}");
        builder.AppendLine($"结果：{(result.IsSuccess ? "成功" : "失败")}");
        if (result.StatusCode is not null) builder.AppendLine($"HTTP：{result.StatusCode} {result.ReasonPhrase}");
        builder.AppendLine("尝试 URL：");
        foreach (var url in result.AttemptedUrls) builder.AppendLine($"- {url}");
        if (!string.IsNullOrWhiteSpace(result.SuccessfulUrl)) builder.AppendLine($"命中 URL：{result.SuccessfulUrl}");
        if (result.IsSuccess) builder.AppendLine($"返回图片数：{result.ImageCount}");
        else if (!string.IsNullOrWhiteSpace(result.ErrorText)) builder.AppendLine($"错误：{result.ErrorText}");
        return builder.ToString().Trim();
    }

    private static string BuildAudioProbeDetails(AiAudioTranscriptionProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("结论：当前语音转写接口可用。\n");
        builder.AppendLine("本次只做快速探活：上传一段内置短 WAV 测试音频，验证上传、鉴权和资料包路由是否打通。\n");
        builder.AppendLine($"服务返回：{ExtractAudioProbeResponseSummary(result)}");
        builder.AppendLine($"命中地址：{result.FinalUrl}");
        builder.AppendLine();
        builder.AppendLine("调试信息（排错时再看）");
        builder.AppendLine($"- 解析片段数：{result.CueCount}");
        builder.AppendLine($"- 原始响应预览：{TrimPreview(result.RawJson, 240)}");
        return builder.ToString().Trim();
    }

    private static string DescribeAuth(AiEndpoint endpoint)
    {
        if (endpoint.AuthMode == AzureAuthMode.AAD) return "AAD Bearer";
        var mode = EndpointProfileUrlBuilder.GetEffectiveApiKeyHeaderMode(
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.ApiKeyHeaderMode,
            endpoint.IsAzureEndpoint || endpoint.EndpointType == EndpointApiType.ApiManagementGateway);
        return mode == ApiKeyHeaderMode.ApiKeyHeader ? "api-key Header" : "Authorization: Bearer";
    }

    private static string DescribeTextProtocol(AiEndpoint endpoint)
    {
        var previewUrls = EndpointProfileUrlBuilder.BuildConfiguredTextUrlCandidates(endpoint.BaseUrl ?? string.Empty, endpoint.ProfileId, endpoint.EndpointType, endpoint.TextApiProtocolMode, endpoint.IsAzureEndpoint, endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Text))?.DeploymentName, endpoint.ApiVersion);
        if (previewUrls.Count == 0) return "资料包未声明";
        return EndpointProfileUrlBuilder.GetEffectiveTextProtocol(endpoint.ProfileId, endpoint.EndpointType, endpoint.TextApiProtocolMode, endpoint.IsAzureEndpoint) switch
        {
            TextApiProtocolMode.Responses => "responses（资料包）",
            TextApiProtocolMode.ChatCompletionsRaw => "chat/completions（资料包）",
            TextApiProtocolMode.ChatCompletionsV1 => "v1/chat/completions（资料包）",
            _ => endpoint.IsAzureEndpoint ? "Azure deployments（资料包）" : "资料包已声明，但未标注首选协议"
        };
    }

    private static string DescribeImageRoute(AiEndpoint endpoint)
    {
        var deploymentName = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.Image))?.DeploymentName;
        var urls = EndpointProfileUrlBuilder.BuildConfiguredImageGenerateUrlCandidates(endpoint.BaseUrl ?? string.Empty, endpoint.ProfileId, endpoint.EndpointType, endpoint.ImageApiRouteMode, deploymentName, endpoint.ApiVersion);
        return urls.Count == 0 ? "资料包未声明" : "资料包候选路线";
    }

    private static string DescribeAudioRoute(AiEndpoint endpoint)
    {
        var deploymentName = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.SpeechToText))?.DeploymentName;
        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            deploymentName = endpoint.Models.FirstOrDefault(model => model.Capabilities.HasFlag(ModelCapability.SpeechToText))?.ModelId;
        }

        var urls = EndpointProfileUrlBuilder.BuildConfiguredAudioTranscriptionUrlCandidates(endpoint.BaseUrl ?? string.Empty, endpoint.ProfileId, endpoint.EndpointType, deploymentName, endpoint.ApiVersion);
        return urls.Count == 0 ? "资料包未声明" : "资料包候选路线";
    }

    private static string DescribeRealtimeRoute(AiEndpoint endpoint, AiModelEntry model)
    {
        var deploymentName = string.IsNullOrWhiteSpace(model.DeploymentName) ? model.ModelId : model.DeploymentName;
        if (endpoint.IsAzureEndpoint)
        {
            return IsPreviewRealtimeSpeechModel(model.ModelId) || IsPreviewRealtimeSpeechModel(deploymentName)
                ? "Azure OpenAI Preview：/openai/realtime?api-version=2025-04-01-preview&deployment=..."
                : "Azure OpenAI GA：/openai/v1/realtime?model=...";
        }

        return "OpenAI GA：/v1/realtime?model=...";
    }

    private static bool ShouldUseRealtimeSpeechProbe(ModelRuntimeResolution runtime)
    {
        return LooksLikeRealtimeSpeechModel(runtime.ModelId)
               || LooksLikeRealtimeSpeechModel(runtime.EffectiveDeploymentName);
    }

    private static bool LooksLikeRealtimeSpeechModel(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.IndexOf("realtime", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsPreviewRealtimeSpeechModel(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.IndexOf("realtime", StringComparison.OrdinalIgnoreCase) >= 0
           && value.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0;

    private async Task ApplyRealtimeAuthenticationAsync(
        ClientWebSocket socket,
        ModelRuntimeResolution runtime,
        RealtimeConnectionSpec spec,
        CancellationToken cancellationToken)
    {
        switch (spec.AuthTransportKind)
        {
            case RealtimeAuthTransportKind.AuthorizationBearer:
                if (runtime.AzureAuthMode == AzureAuthMode.AAD)
                {
                    var provider = await CreateTokenProviderAsync(runtime, cancellationToken);
                    var token = await provider!.GetTokenAsync(cancellationToken);
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                }
                else
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {runtime.ApiKey}");
                }
                break;

            case RealtimeAuthTransportKind.ApiKeyHeader:
                socket.Options.SetRequestHeader("api-key", runtime.ApiKey);
                break;
        }
    }

    private static async Task SendRealtimeProbeSessionUpdateAsync(
        ClientWebSocket socket,
        RealtimeConnectionSpec spec,
        CancellationToken cancellationToken)
    {
        object payload = spec.RouteKind == RealtimeEndpointRouteKind.AzureOpenAiPreview
            ? new
            {
                type = "session.update",
                session = new
                {
                    modalities = new[] { "text" },
                    instructions = "你现在是连通性测试助手。收到任何输入后，只回复 OK。",
                    max_response_output_tokens = 32
                }
            }
            : new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime",
                    instructions = "你现在是连通性测试助手。收到任何输入后，只回复 OK。",
                    output_modalities = new[] { "text" }
                }
            };

        await SendRealtimeJsonAsync(socket, payload, cancellationToken);
    }

    private static async Task SendRealtimeProbeConversationAsync(ClientWebSocket socket, RealtimeConnectionSpec spec, CancellationToken cancellationToken)
    {
        await SendRealtimeJsonAsync(socket, new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[]
                {
                    new
                    {
                        type = "input_text",
                        text = "Reply with OK only."
                    }
                }
            }
        }, cancellationToken);

        object payload = spec.RouteKind == RealtimeEndpointRouteKind.AzureOpenAiPreview
            ? new
            {
                type = "response.create",
                response = new
                {
                    modalities = new[] { "text" },
                    instructions = "Reply with OK only."
                }
            }
            : new
            {
                type = "response.create",
                response = new
                {
                    output_modalities = new[] { "text" },
                    instructions = "Reply with OK only."
                }
            };

        await SendRealtimeJsonAsync(socket, payload, cancellationToken);
    }

    private static async Task SendRealtimeJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<RealtimeProbeResult> ReceiveRealtimeProbeResultAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var bufferOwner = MemoryPool<byte>.Shared.Rent(16 * 1024);
        var buffer = bufferOwner.Memory;
        var responseText = new StringBuilder();
        var eventTypes = new List<string>();
        var rawEventPreviews = new List<string>();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return RealtimeProbeResult.Fail(
                        "服务端提前关闭了 Realtime WebSocket，会话未完成。",
                        string.Join(" -> ", eventTypes));
                }

                ms.Write(buffer.Span[..result.Count]);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var rawJson = Encoding.UTF8.GetString(ms.ToArray());
            ms.Position = 0;
            using var doc = await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(type))
            {
                eventTypes.Add(type);
            }

            CaptureRealtimeRawEventPreview(rawEventPreviews, type, rawJson);

            switch (type)
            {
                case "response.text.delta":
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var deltaElement) && deltaElement.ValueKind == JsonValueKind.String)
                    {
                        responseText.Append(deltaElement.GetString());
                    }
                    break;

                case "response.text.done":
                    if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                    {
                        var text = textElement.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            responseText.Clear();
                            responseText.Append(text.Trim());
                        }
                    }
                    break;

                case "response.done":
                    var finalText = responseText.ToString().Trim();
                    return RealtimeProbeResult.Success(
                        "Realtime 会话已建立并成功返回响应。",
                        string.Join(" -> ", eventTypes),
                        string.IsNullOrWhiteSpace(finalText) ? "（空）" : finalText,
                        string.Join("\n", rawEventPreviews));

                case "error":
                    var message = TryGetNestedString(root, "error", "message");
                    return RealtimeProbeResult.Fail(
                        $"Realtime 服务返回错误：{message}",
                        string.Join(" -> ", eventTypes),
                        string.Join("\n", rawEventPreviews));
            }
        }

        return RealtimeProbeResult.Fail(
            "Realtime 会话在等待响应时超时。",
            string.Join(" -> ", eventTypes),
            string.Join("\n", rawEventPreviews));
    }

    private static void CaptureRealtimeRawEventPreview(List<string> rawEventPreviews, string eventType, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(rawJson))
        {
            return;
        }

        if (eventType is not ("response.text.delta" or "response.output_text.delta" or "response.text.done" or "response.done" or "error"))
        {
            return;
        }

        var preview = $"- {eventType}: {TrimPreview(rawJson, 320)}";
        rawEventPreviews.Add(preview);

        if (rawEventPreviews.Count > 4)
        {
            rawEventPreviews.RemoveAt(0);
        }
    }

    private static string TryGetNestedString(JsonElement element, string outerName, string innerName)
    {
        if (!element.TryGetProperty(outerName, out var outer) || outer.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return outer.TryGetProperty(innerName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string BuildRealtimeProbeDetails(RealtimeConnectionSpec spec, RealtimeProbeResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("结论：当前 Realtime 语音模型可用。\n");
        builder.AppendLine("本次只做快速探活：建立官方 Realtime WebSocket 会话，并发送一条极短文本消息验证会话与响应链路。\n");
        builder.AppendLine($"连接模式：{DescribeRealtimeSpec(spec)}");

        if (!string.IsNullOrWhiteSpace(result.ResponsePreview))
        {
            builder.AppendLine($"模型回包：已按探活指令返回确认文本“{result.ResponsePreview}”");
            builder.AppendLine("说明：这里显示的不是服务端固定状态码，而是探活时要求模型返回的一条最小文本响应，用来证明会话收发链路正常。");
        }

        if (!string.IsNullOrWhiteSpace(result.RawMessage))
        {
            builder.AppendLine($"补充说明：{result.RawMessage}");
        }

        if (!string.IsNullOrWhiteSpace(result.EventTrail))
        {
            builder.AppendLine();
            builder.AppendLine("调试信息（排错时再看）");
            builder.AppendLine($"- 协议事件：{result.EventTrail}");

            if (!string.IsNullOrWhiteSpace(result.RawPayloadPreview))
            {
                builder.AppendLine("- 原始事件片段：");
                builder.AppendLine(result.RawPayloadPreview);
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildAudioProbeSummary(AiAudioTranscriptionProbeResult result)
        => $"语音转写测试通过：已成功收到测试转写结果（{ExtractAudioProbeResponseSummary(result)}）。";

    private static string BuildRealtimeProbeSummary(string responsePreview)
        => string.IsNullOrWhiteSpace(responsePreview)
            ? "Realtime 语音模型测试通过：已成功建立会话并收到服务响应。"
            : $"Realtime 语音模型测试通过：已成功建立会话，模型返回确认文本“{responsePreview}”。";

    private static string ExtractAudioProbeResponseSummary(AiAudioTranscriptionProbeResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RawJson))
        {
            return result.CueCount > 0 ? $"返回了 {result.CueCount} 个转写片段" : "接口调用成功，但返回体为空";
        }

        try
        {
            using var document = JsonDocument.Parse(result.RawJson);
            var root = document.RootElement;

            if (root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return $"收到测试文本“{TrimPreview(text, 48)}”";
                }
            }
        }
        catch
        {
        }

        return result.CueCount > 0 ? $"返回了 {result.CueCount} 个转写片段" : "接口调用成功";
    }

    private static string DescribeRealtimeSpec(RealtimeConnectionSpec spec)
        => spec.RouteKind switch
        {
            RealtimeEndpointRouteKind.AzureOpenAiPreview => "Azure OpenAI Realtime Preview（/openai/realtime?api-version=2025-04-01-preview&deployment=...）",
            RealtimeEndpointRouteKind.AzureOpenAiGa => "Azure OpenAI Realtime GA（/openai/v1/realtime?model=...）",
            RealtimeEndpointRouteKind.OpenAiGa => "OpenAI Realtime GA（/v1/realtime?model=...）",
            _ => "Realtime WebSocket"
        };

    private static string FormatExceptionDetails(Exception ex)
    {
        var builder = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current != null && depth < 5)
        {
            if (depth > 0)
            {
                builder.AppendLine();
                builder.AppendLine("---- Inner Exception ----");
            }
            builder.Append(current.Message);
            if (current is HttpRequestException) break;
            current = current.InnerException;
            depth++;
        }
        return builder.ToString().Trim();
    }

    private static string CreateAudioProbeFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TrueFluentPro", "EndpointBatchTest");
        Directory.CreateDirectory(tempDir);

        var filePath = Path.Combine(tempDir, $"audio_probe_{Guid.NewGuid():N}.wav");
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double durationSeconds = 2.2;
        var totalSamples = (int)(sampleRate * durationSeconds);
        var blockAlign = (short)(channels * (bitsPerSample / 8));
        var byteRate = sampleRate * blockAlign;
        var dataSize = totalSamples * blockAlign;

        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);

        for (var i = 0; i < totalSamples; i++)
        {
            var t = i / (double)sampleRate;
            var burst = (t >= 0.18 && t <= 0.52)
                        || (t >= 0.76 && t <= 1.12)
                        || (t >= 1.38 && t <= 1.78);
            var envelope = burst ? Math.Sin(Math.PI * Math.Clamp((t % 0.4) / 0.4, 0, 1)) : 0;
            var sample = burst
                ? Math.Sin(2 * Math.PI * 440 * t) * 0.22 * envelope
                : 0;
            writer.Write((short)(sample * short.MaxValue));
        }

        return filePath;
    }

    private static string TrimPreview(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（空）";
        }

        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record CapabilityTestPlan(ModelCapability Capability, string CapabilityDisplayName, string Url, string RouteLabel, string RequestUrlText, string RequestSummary);

    private sealed record RealtimeProbeResult(bool IsSuccess, string RawMessage, string EventTrail, string ResponsePreview, string RawPayloadPreview)
    {
        public string Details => string.IsNullOrWhiteSpace(EventTrail)
            ? RawMessage
            : string.IsNullOrWhiteSpace(RawMessage)
                ? $"事件轨迹：{EventTrail}"
                : $"{RawMessage}\n事件轨迹：{EventTrail}";

        public static RealtimeProbeResult Success(string rawMessage, string eventTrail, string responsePreview, string rawPayloadPreview = "")
            => new(true, rawMessage, eventTrail, responsePreview, rawPayloadPreview);

        public static RealtimeProbeResult Fail(string rawMessage, string eventTrail = "", string rawPayloadPreview = "")
            => new(false, rawMessage, eventTrail, string.Empty, rawPayloadPreview);
    }
}
