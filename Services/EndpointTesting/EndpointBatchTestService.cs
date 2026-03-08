using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;
using TrueFluentPro.Services.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointTesting;

public sealed class EndpointBatchTestService : IEndpointBatchTestService
{
    private static readonly ModelCapability[] CapabilityOrder =
    [
        ModelCapability.Text,
        ModelCapability.Image,
        ModelCapability.Video
    ];

    private const int MaxConcurrency = 6;

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
        var pendingTasks = new List<Task<EndpointBatchTestItem>>();
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
            var models = endpoint.Models?.ToList() ?? new List<AiModelEntry>();
            if (models.Count == 0)
            {
                var noModelItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "整体", "", "当前选中的终结点未配置任何模型。", "请先为这个终结点添加至少一个文字、图片或视频模型。");
                immediateItems.Add(noModelItem);
                progressItems.Add(ToProgressItem(noModelItem));
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
            }

            foreach (var model in models)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    var skippedItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, GetCapabilityNameOrFallback(model.Capabilities), "", "模型 ID 未填写，未执行测试。", "本次只测试当前选中终结点里已经明确写好的模型；空白模型行会直接跳过。");
                    immediateItems.Add(skippedItem);
                    progressItems.Add(ToProgressItem(skippedItem));
                    continue;
                }

                var capabilities = CapabilityOrder.Where(capability => model.Capabilities.HasFlag(capability)).ToList();
                if (capabilities.Count == 0)
                {
                    var unsupportedItem = CreateSkippedItem(order++, endpoint.Id, GetEndpointName(endpoint), endpoint.EndpointTypeDisplayName, "未知能力", model.ModelId, "模型能力未标记为文字、图片或视频，未执行测试。", "请先为该模型选择正确的能力类型，再重新发起测试。");
                    immediateItems.Add(unsupportedItem);
                    progressItems.Add(ToProgressItem(unsupportedItem));
                    continue;
                }

                foreach (var capability in capabilities)
                {
                    var plans = BuildCapabilityTestPlans(endpoint, model, capability, config.MediaGenConfig);
                    if (plans.Count == 0)
                    {
                        var failedItem = CreateProfileMissingItem(order++, endpoint, model, capability, config.MediaGenConfig);
                        immediateItems.Add(failedItem);
                        progressItems.Add(ToProgressItem(failedItem));
                        continue;
                    }

                    foreach (var plan in plans)
                    {
                        var currentOrder = order++;
                        var pendingItem = CreatePendingProgressItem(currentOrder, endpoint, model, plan);
                        progressItems.Add(pendingItem);
                        pendingTasks.Add(RunWithGateAsync(gate, async () =>
                        {
                            ReplaceProgressItem(progressItems, CreateRunningProgressItem(pendingItem));
                            ReportProgress(progress, startedAt, endpoint, progressItems, false);
                            var result = await TestCapabilityAsync(currentOrder, endpoint, model, plan, config, cancellationToken);
                            ReplaceProgressItem(progressItems, ToProgressItem(result));
                            ReportProgress(progress, startedAt, endpoint, progressItems, false);
                            return result;
                        }, cancellationToken));
                    }
                }
            }

            if (progressItems.Count > 0)
            {
                ReportProgress(progress, startedAt, endpoint, progressItems, false);
            }
        }

        var completedItems = pendingTasks.Count == 0 ? Array.Empty<EndpointBatchTestItem>() : await Task.WhenAll(pendingTasks);
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

    private static async Task<EndpointBatchTestItem> RunWithGateAsync(SemaphoreSlim gate, Func<Task<EndpointBatchTestItem>> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
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

    private static async Task<EndpointBatchTestItem> TestTextAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiInsightService(tokenProvider);
        var request = runtime.CreateChatRequest(summaryEnableReasoning: false);
        var responseBuilder = new StringBuilder();
        AiRequestTrace? requestTrace = null;

        await service.StreamChatAsync(
            request,
            "你是连通性测试助手，请用简短中文直接回复。",
            "你好",
            chunk => { if (responseBuilder.Length < 160) responseBuilder.Append(chunk); },
            timeoutCts.Token,
            AiChatProfile.Quick,
            enableReasoning: false,
            onTrace: trace => requestTrace = trace,
            urlCandidatesOverride: new[] { plan.Url },
            allowNextUrlRetry: false,
            allowApimSubscriptionKeyQueryRetry: false);

        stopwatch.Stop();
        var requestUrlText = BuildTextRequestUrlText(plan.Url, requestTrace);
        var responseText = responseBuilder.ToString().Trim();

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return CreateFailedItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, "文字测试返回为空。", "请求已成功发出，但没有收到有效文本响应；请检查模型是否支持当前资料包声明的文本接口。\n\n原始返回片段为空。");
        }

        return CreateSuccessItem(order, runtime.EndpointId, endpointName, endpointTypeName, plan.CapabilityDisplayName, runtime.ModelId, requestUrlText, plan.RequestSummary, stopwatch.Elapsed, "文字测试通过。", $"返回片段：{responseText}");
    }

    private static async Task<EndpointBatchTestItem> TestImageAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, MediaGenConfig sourceConfig, Stopwatch stopwatch, CancellationToken cancellationToken)
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

    private static async Task<EndpointBatchTestItem> TestVideoAsync(int order, ModelRuntimeResolution runtime, CapabilityTestPlan plan, string endpointName, string endpointTypeName, MediaGenConfig sourceConfig, Stopwatch stopwatch, CancellationToken cancellationToken)
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

    private static async Task<AzureTokenProvider?> CreateTokenProviderAsync(ModelRuntimeResolution runtime, CancellationToken cancellationToken)
    {
        if (runtime.AzureAuthMode != AzureAuthMode.AAD) return null;
        var tokenProvider = new AzureTokenProvider(runtime.ProfileKey);
        var loggedIn = await tokenProvider.TrySilentLoginAsync(runtime.AzureTenantId, runtime.AzureClientId);
        if (!loggedIn) throw new InvalidOperationException("AAD 未登录，请先在当前终结点里完成登录后再测试。");
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
            ModelCapability.Video => BuildRoutePlans(capability, "视频（创建）", BuildStrictVideoCreateUrls(runtime, mediaGenConfig), baseSummary + "\n视频测试：仅验证创建接口，不再执行轮询和下载。", "POST"),
            _ => Array.Empty<CapabilityTestPlan>()
        };
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
        var capabilitySuffix = capability switch
        {
            ModelCapability.Text => $"文本协议：{DescribeTextProtocol(endpoint)}",
            ModelCapability.Image => $"图片路由：{DescribeImageRoute(endpoint)}",
            ModelCapability.Video => BuildVideoModeSummary(mediaGenConfig.VideoApiMode, effectiveVideoMode),
            _ => capability.ToString()
        };
        var versionSummary = BuildApiVersionSummary(endpoint, effectiveVideoMode);
        return $"认证：{auth}\n基础地址：{endpoint.BaseUrl?.Trim() ?? "未填写"}\n{deployment}\nAPI版本字段：{rawApiVersion}\n{versionSummary}\n{capabilitySuffix}";
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

    private static string BuildImageProbeRequestUrlText(string plannedUrl, AiImageGenService.ImageRouteProbeResult result)
    {
        var lines = new List<string> { $"POST {(string.IsNullOrWhiteSpace(result.SuccessfulUrl) ? plannedUrl : result.SuccessfulUrl)}" };
        if (result.AttemptedUrls.Count > 1)
        {
            lines.Add("尝试过的 URL：");
            lines.AddRange(result.AttemptedUrls.Select(url => $"- {url}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

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

    private static string BuildImageProbeDetails(AiImageGenService.ImageRouteProbeResult result)
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

    private static string DescribeAuth(AiEndpoint endpoint)
    {
        if (endpoint.AuthMode == AzureAuthMode.AAD) return "AAD Bearer";
        var mode = endpoint.ApiKeyHeaderMode;
        if (mode == ApiKeyHeaderMode.Auto)
        {
            mode = endpoint.IsAzureEndpoint || endpoint.EndpointType == EndpointApiType.ApiManagementGateway ? ApiKeyHeaderMode.ApiKeyHeader : ApiKeyHeaderMode.Bearer;
        }
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

    private sealed record CapabilityTestPlan(ModelCapability Capability, string CapabilityDisplayName, string Url, string RouteLabel, string RequestUrlText, string RequestSummary);
}
