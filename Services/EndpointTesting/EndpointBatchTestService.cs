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
            var missingItem = CreateSkippedItem(
                order++,
                endpoint.Id,
                GetEndpointName(endpoint),
                endpoint.EndpointTypeDisplayName,
                "整体",
                "",
                "当前选中的终结点已不在配置列表中，未执行测试。",
                "请先保存或重新选择一个仍存在的终结点后再测试。");
            immediateItems.Add(missingItem);
            progressItems.Add(ToProgressItem(missingItem));
            ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);
        }
        else if (!endpoint.IsEnabled)
        {
            var disabledItem = CreateSkippedItem(
                order++,
                endpoint.Id,
                GetEndpointName(endpoint),
                endpoint.EndpointTypeDisplayName,
                "整体",
                "",
                "当前选中的终结点已停用，未执行测试。",
                "如需验证连通性，请先启用该终结点后再运行测试。");
            immediateItems.Add(disabledItem);
            progressItems.Add(ToProgressItem(disabledItem));
            ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);
        }
        else
        {
            var models = endpoint.Models?.ToList() ?? new List<AiModelEntry>();
            if (models.Count == 0)
            {
                var noModelItem = CreateSkippedItem(
                    order++,
                    endpoint.Id,
                    GetEndpointName(endpoint),
                    endpoint.EndpointTypeDisplayName,
                    "整体",
                    "",
                    "当前选中的终结点未配置任何模型。",
                    "请先为这个终结点添加至少一个文字、图片或视频模型。");
                immediateItems.Add(noModelItem);
                progressItems.Add(ToProgressItem(noModelItem));
                ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);
            }

            foreach (var model in models)
            {
                if (string.IsNullOrWhiteSpace(model.ModelId))
                {
                    var skippedItem = CreateSkippedItem(
                        order++,
                        endpoint.Id,
                        GetEndpointName(endpoint),
                        endpoint.EndpointTypeDisplayName,
                        GetCapabilityNameOrFallback(model.Capabilities),
                        "",
                        "模型 ID 未填写，未执行测试。",
                        "本次只测试当前选中终结点里已经明确写好的模型；空白模型行会直接跳过。"
                    );
                    immediateItems.Add(skippedItem);
                    progressItems.Add(ToProgressItem(skippedItem));
                    continue;
                }

                var capabilities = CapabilityOrder
                    .Where(capability => model.Capabilities.HasFlag(capability))
                    .ToList();

                if (capabilities.Count == 0)
                {
                    var unsupportedItem = CreateSkippedItem(
                        order++,
                        endpoint.Id,
                        GetEndpointName(endpoint),
                        endpoint.EndpointTypeDisplayName,
                        "未知能力",
                        model.ModelId,
                        "模型能力未标记为文字、图片或视频，未执行测试。",
                        "请先为该模型选择正确的能力类型，再重新发起测试。"
                    );
                    immediateItems.Add(unsupportedItem);
                    progressItems.Add(ToProgressItem(unsupportedItem));
                    continue;
                }

                foreach (var capability in capabilities)
                {
                    var currentOrder = order++;
                    var pendingItem = CreatePendingProgressItem(currentOrder, endpoint, model, capability, config.MediaGenConfig);
                    progressItems.Add(pendingItem);
                    pendingTasks.Add(RunWithGateAsync(
                        gate,
                        async () =>
                        {
                            ReplaceProgressItem(progressItems, CreateRunningProgressItem(pendingItem));
                            ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);

                            var result = await TestCapabilityAsync(currentOrder, endpoint, model, capability, config, cancellationToken);
                            ReplaceProgressItem(progressItems, ToProgressItem(result));
                            ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);
                            return result;
                        },
                        cancellationToken));
                }
            }

            if (progressItems.Count > 0)
            {
                ReportProgress(progress, startedAt, endpoint, progressItems, isCompleted: false);
            }
        }

        var completedItems = pendingTasks.Count == 0
            ? Array.Empty<EndpointBatchTestItem>()
            : await Task.WhenAll(pendingTasks);

        stopwatch.Stop();

        var report = new EndpointBatchTestReport
        {
            StartedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Items = immediateItems
                .Concat(completedItems)
                .OrderBy(item => item.Order)
                .ToList()
        };

        var finalProgressItems = report.Items
            .Select(ToProgressItem)
            .OrderBy(item => item.Order)
            .ToList();
        ReportProgress(progress, startedAt, endpoint, finalProgressItems, isCompleted: true);

        return report;
    }

    private static async Task<EndpointBatchTestItem> RunWithGateAsync(
        SemaphoreSlim gate,
        Func<Task<EndpointBatchTestItem>> action,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<EndpointBatchTestItem> TestCapabilityAsync(
        int order,
        AiEndpoint endpoint,
        AiModelEntry model,
        ModelCapability capability,
        AzureSpeechConfig config,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var endpointName = GetEndpointName(endpoint);
        var endpointTypeName = endpoint.EndpointTypeDisplayName;
        var capabilityName = GetCapabilityName(capability);
        var requestSummary = BuildRequestSummary(endpoint, model, capability, config.MediaGenConfig);

        try
        {
            ValidateEndpoint(endpoint, model, capability);

            var runtime = new ModelRuntimeResolution
            {
                Endpoint = endpoint,
                Model = model,
                Capability = capability
            };

            return capability switch
            {
                ModelCapability.Text => await TestTextAsync(order, runtime, endpointName, endpointTypeName, requestSummary, stopwatch, cancellationToken),
                ModelCapability.Image => await TestImageAsync(order, runtime, endpointName, endpointTypeName, requestSummary, config.MediaGenConfig, stopwatch, cancellationToken),
                ModelCapability.Video => await TestVideoAsync(order, runtime, endpointName, endpointTypeName, requestSummary, config.MediaGenConfig, stopwatch, cancellationToken),
                _ => CreateFailedItem(order, endpoint.Id, endpointName, endpointTypeName, capabilityName, model.ModelId, requestSummary, stopwatch.Elapsed, "暂不支持该测试类型。", "当前仅支持文字、图片、视频三类连通性测试。")
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return CreateFailedItem(
                order,
                endpoint.Id,
                endpointName,
                endpointTypeName,
                capabilityName,
                model.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                $"{capabilityName}测试超时。",
                "请求已达到等待上限，请检查终结点地址、模型部署名、网络连通性或权限配置。");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CreateFailedItem(
                order,
                endpoint.Id,
                endpointName,
                endpointTypeName,
                capabilityName,
                model.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                $"{capabilityName}测试失败。",
                FormatExceptionDetails(ex));
        }
    }

    private static async Task<EndpointBatchTestItem> TestTextAsync(
        int order,
        ModelRuntimeResolution runtime,
        string endpointName,
        string endpointTypeName,
        string requestSummary,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiInsightService(tokenProvider);
        var request = runtime.CreateChatRequest(summaryEnableReasoning: false);
        var responseBuilder = new StringBuilder();

        await service.StreamChatAsync(
            request,
            "你是连通性测试助手，请用简短中文直接回复。",
            "你好",
            chunk =>
            {
                if (responseBuilder.Length < 160)
                {
                    responseBuilder.Append(chunk);
                }
            },
            timeoutCts.Token,
            AiChatProfile.Quick,
            enableReasoning: false);

        stopwatch.Stop();

        var responseText = responseBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                GetCapabilityName(ModelCapability.Text),
                runtime.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                "文字测试返回为空。",
                "请求已成功发出，但没有收到有效文本响应；请检查模型是否支持当前文本接口。\n\n原始返回片段为空。");
        }

        return CreateSuccessItem(
            order,
            runtime.EndpointId,
            endpointName,
            endpointTypeName,
            GetCapabilityName(ModelCapability.Text),
            runtime.ModelId,
            requestSummary,
            stopwatch.Elapsed,
            "文字测试通过。",
            $"返回片段：{responseText}");
    }

    private static async Task<EndpointBatchTestItem> TestImageAsync(
        int order,
        ModelRuntimeResolution runtime,
        string endpointName,
        string endpointTypeName,
        string requestSummary,
        MediaGenConfig sourceConfig,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(runtime.EndpointType == EndpointApiType.ApiManagementGateway ? 90 : 45));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiImageGenService();
        service.SetTokenProvider(tokenProvider);

        var requestConfig = runtime.CreateRequestConfig();
        var genConfig = CreateImageTestConfig(sourceConfig, runtime.ModelId);

        if (runtime.EndpointType == EndpointApiType.ApiManagementGateway)
        {
            var routeResults = new List<AiImageGenService.ImageRouteProbeResult>();
            foreach (var probePlan in BuildApimImageGenerateProbePlans(service, requestConfig))
            {
                var routeResult = await service.ProbeGenerateCandidateUrlsAsync(
                    probePlan.Config,
                    "请生成一只卡通兔子。",
                    genConfig,
                    probePlan.RouteLabel,
                    probePlan.CandidateUrls,
                    timeoutCts.Token);
                routeResults.Add(routeResult);
            }

            stopwatch.Stop();

            var successCount = routeResults.Count(item => item.IsSuccess && item.ImageCount > 0);
            var details = BuildApimImageRouteProbeDetails(routeResults);

            if (successCount == routeResults.Count && successCount > 0)
            {
                return CreateSuccessItem(
                    order,
                    runtime.EndpointId,
                    endpointName,
                    endpointTypeName,
                    GetCapabilityName(ModelCapability.Image),
                    runtime.ModelId,
                    requestSummary,
                    stopwatch.Elapsed,
                        $"图片测试通过，APIM {routeResults.Count} 条图片路由都可用。",
                    details);
            }

            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                GetCapabilityName(ModelCapability.Image),
                runtime.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                $"图片测试存在兼容性风险：{successCount}/{routeResults.Count} 条 APIM 图片路由通过。",
                details);
        }

        var result = await service.GenerateImagesAsync(
            requestConfig,
            "请生成一只卡通兔子。",
            genConfig,
            timeoutCts.Token);

        stopwatch.Stop();

        if (result.Images.Count == 0)
        {
            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                GetCapabilityName(ModelCapability.Image),
                runtime.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                "图片测试未拿到任何结果。",
                "图片接口请求已完成，但返回结果中没有可用图片数据。请检查模型能力和网关返回格式。");
        }

        return CreateSuccessItem(
            order,
            runtime.EndpointId,
            endpointName,
            endpointTypeName,
            GetCapabilityName(ModelCapability.Image),
            runtime.ModelId,
            requestSummary,
            stopwatch.Elapsed,
            $"图片测试通过，已返回 {result.Images.Count} 张图片。",
            $"生成耗时：{result.GenerateSeconds:F1}s\n下载耗时：{result.DownloadSeconds:F1}s");
    }

    private static async Task<EndpointBatchTestItem> TestVideoAsync(
        int order,
        ModelRuntimeResolution runtime,
        string endpointName,
        string endpointTypeName,
        string requestSummary,
        MediaGenConfig sourceConfig,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

        var tokenProvider = await CreateTokenProviderAsync(runtime, timeoutCts.Token);
        var service = new AiVideoGenService();
        service.SetTokenProvider(tokenProvider);

        var requestConfig = runtime.CreateRequestConfig();
        var genConfig = CreateVideoTestConfig(sourceConfig, runtime.ModelId);
        var videoId = await service.CreateVideoAsync(
            requestConfig,
            "请生成一只卡通兔子。",
            genConfig,
            referenceImagePath: null,
            timeoutCts.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), timeoutCts.Token);
        var (status, progress, generationId, failureReason) = await service.PollStatusDetailsAsync(
            requestConfig,
            videoId,
            timeoutCts.Token,
            genConfig.VideoApiMode);

        stopwatch.Stop();

        if (IsVideoFailureStatus(status))
        {
            var failureText = string.IsNullOrWhiteSpace(failureReason)
                ? "视频任务进入失败状态。"
                : $"视频任务失败：{failureReason}";

            return CreateFailedItem(
                order,
                runtime.EndpointId,
                endpointName,
                endpointTypeName,
                GetCapabilityName(ModelCapability.Video),
                runtime.ModelId,
                requestSummary,
                stopwatch.Elapsed,
                "视频测试失败。",
                $"video_id：{videoId}\nstatus：{status}\nprogress：{progress}%\n{failureText}");
        }

        var detail = new StringBuilder();
        detail.AppendLine($"video_id：{videoId}");
        detail.AppendLine($"status：{status}");
        detail.AppendLine($"progress：{progress}%");
        detail.Append($"generation_id：{(string.IsNullOrWhiteSpace(generationId) ? "未返回" : generationId)}");

        return CreateSuccessItem(
            order,
            runtime.EndpointId,
            endpointName,
            endpointTypeName,
            GetCapabilityName(ModelCapability.Video),
            runtime.ModelId,
            requestSummary,
            stopwatch.Elapsed,
            "视频联通测试成功：任务已提交，并成功取回一次状态。",
            detail.ToString());
    }

    private static void ValidateEndpoint(AiEndpoint endpoint, AiModelEntry model, ModelCapability capability)
    {
        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
        {
            throw new InvalidOperationException("未填写 API 地址。");
        }

        if (endpoint.AuthMode == AzureAuthMode.AAD)
        {
            if (!endpoint.IsAzureEndpoint)
            {
                throw new InvalidOperationException("当前仅 Azure OpenAI 终结点支持 AAD 测试。");
            }

            if (string.IsNullOrWhiteSpace(endpoint.AzureTenantId))
            {
                throw new InvalidOperationException("AAD 测试缺少 Tenant ID。");
            }
        }
        else if (string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            throw new InvalidOperationException("未填写 API 密钥。");
        }

        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            throw new InvalidOperationException($"{GetCapabilityName(capability)}模型的模型 ID 为空。");
        }
    }

    private static async Task<AzureTokenProvider?> CreateTokenProviderAsync(ModelRuntimeResolution runtime, CancellationToken cancellationToken)
    {
        if (runtime.AzureAuthMode != AzureAuthMode.AAD)
        {
            return null;
        }

        var tokenProvider = new AzureTokenProvider(runtime.ProfileKey);
        var loggedIn = await tokenProvider.TrySilentLoginAsync(runtime.AzureTenantId, runtime.AzureClientId);
        if (!loggedIn)
        {
            throw new InvalidOperationException("AAD 未登录，请先在当前终结点里完成登录后再测试。");
        }

        return tokenProvider;
    }

    private static MediaGenConfig CreateImageTestConfig(MediaGenConfig source, string modelId)
    {
        return new MediaGenConfig
        {
            ImageModel = modelId,
            ImageSize = string.IsNullOrWhiteSpace(source.ImageSize) ? "1024x1024" : source.ImageSize,
            ImageQuality = string.IsNullOrWhiteSpace(source.ImageQuality) ? "medium" : source.ImageQuality,
            ImageFormat = string.IsNullOrWhiteSpace(source.ImageFormat) ? "png" : source.ImageFormat,
            ImageCount = 1
        };
    }

    private static MediaGenConfig CreateVideoTestConfig(MediaGenConfig source, string modelId)
    {
        return new MediaGenConfig
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
    }

    private static EndpointBatchTestItem CreateSuccessItem(
        int order,
        string endpointId,
        string endpointName,
        string endpointTypeName,
        string capabilityName,
        string modelId,
        string requestSummary,
        TimeSpan duration,
        string summary,
        string details)
    {
        return new EndpointBatchTestItem
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
            RequestSummary = requestSummary,
            Duration = duration
        };
    }

    private static EndpointBatchTestItem CreateFailedItem(
        int order,
        string endpointId,
        string endpointName,
        string endpointTypeName,
        string capabilityName,
        string modelId,
        string requestSummary,
        TimeSpan duration,
        string summary,
        string details)
    {
        return new EndpointBatchTestItem
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
            RequestSummary = requestSummary,
            Duration = duration
        };
    }

    private static EndpointBatchTestItem CreateSkippedItem(
        int order,
        string endpointName,
        string endpointTypeName,
        string summary,
        string details)
    {
        return CreateSkippedItem(order, "", endpointName, endpointTypeName, "整体", "", summary, details);
    }

    private static EndpointBatchTestItem CreateSkippedItem(
        int order,
        string endpointId,
        string endpointName,
        string endpointTypeName,
        string capabilityName,
        string modelId,
        string summary,
        string details)
    {
        return new EndpointBatchTestItem
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
            RequestSummary = "",
            Duration = TimeSpan.Zero
        };
    }

    private static EndpointBatchTestProgressItem CreatePendingProgressItem(
        int order,
        AiEndpoint endpoint,
        AiModelEntry model,
        ModelCapability capability,
        MediaGenConfig mediaGenConfig)
    {
        return new EndpointBatchTestProgressItem
        {
            Order = order,
            EndpointId = endpoint.Id,
            EndpointName = GetEndpointName(endpoint),
            EndpointTypeName = endpoint.EndpointTypeDisplayName,
            CapabilityName = GetCapabilityName(capability),
            ModelId = model.ModelId,
            State = EndpointBatchTestLiveState.Pending,
            Summary = "等待测试开始。",
            Details = "已加入当前终结点的测试队列。",
            RequestSummary = BuildRequestSummary(endpoint, model, capability, mediaGenConfig),
            Duration = TimeSpan.Zero
        };
    }

    private static EndpointBatchTestProgressItem CreateRunningProgressItem(EndpointBatchTestProgressItem source)
    {
        return new EndpointBatchTestProgressItem
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
            RequestSummary = source.RequestSummary,
            Duration = TimeSpan.Zero
        };
    }

    private static EndpointBatchTestProgressItem ToProgressItem(EndpointBatchTestItem item)
    {
        return new EndpointBatchTestProgressItem
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
            RequestSummary = item.RequestSummary,
            Duration = item.Duration
        };
    }

    private static void ReplaceProgressItem(List<EndpointBatchTestProgressItem> items, EndpointBatchTestProgressItem item)
    {
        var index = items.FindIndex(existing => existing.Order == item.Order);
        if (index >= 0)
        {
            items[index] = item;
            return;
        }

        items.Add(item);
    }

    private static void ReportProgress(
        IProgress<EndpointBatchTestProgressSnapshot>? progress,
        DateTimeOffset startedAt,
        AiEndpoint endpoint,
        IReadOnlyList<EndpointBatchTestProgressItem> items,
        bool isCompleted)
    {
        progress?.Report(new EndpointBatchTestProgressSnapshot
        {
            StartedAt = startedAt,
            CompletedAt = isCompleted ? DateTimeOffset.Now : null,
            EndpointId = endpoint.Id,
            EndpointName = GetEndpointName(endpoint),
            IsCompleted = isCompleted,
            Items = items
                .OrderBy(item => item.Order)
                .ToList()
        });
    }

    private static string GetEndpointName(AiEndpoint endpoint)
        => string.IsNullOrWhiteSpace(endpoint.Name) ? endpoint.Id : endpoint.Name;

    private static string GetCapabilityNameOrFallback(ModelCapability capability)
    {
        foreach (var candidate in CapabilityOrder)
        {
            if (capability.HasFlag(candidate))
            {
                return GetCapabilityName(candidate);
            }
        }

        return "未知能力";
    }

    private static string GetCapabilityName(ModelCapability capability)
        => capability switch
        {
            ModelCapability.Text => "文字",
            ModelCapability.Image => "图片",
            ModelCapability.Video => "视频",
            _ => capability.ToString()
        };

    private static string BuildRequestSummary(
        AiEndpoint endpoint,
        AiModelEntry model,
        ModelCapability capability,
        MediaGenConfig mediaGenConfig)
    {
        var auth = DescribeAuth(endpoint);
        var deployment = endpoint.IsAzureEndpoint
            ? $"部署：{(string.IsNullOrWhiteSpace(model.DeploymentName) ? model.ModelId : model.DeploymentName)}"
            : $"模型：{model.ModelId}";

        var capabilitySuffix = capability switch
        {
            ModelCapability.Text => $"文本协议：{DescribeTextProtocol(endpoint)}",
            ModelCapability.Image => $"图片路由：{DescribeImageRoute(endpoint)}",
            ModelCapability.Video => $"视频模式：{mediaGenConfig.VideoApiMode}",
            _ => capability.ToString()
        };

        return $"{auth} | {endpoint.BaseUrl?.Trim() ?? ""} | {deployment} | {capabilitySuffix}";
    }

    private static string DescribeAuth(AiEndpoint endpoint)
    {
        if (endpoint.AuthMode == AzureAuthMode.AAD)
        {
            return "AAD Bearer";
        }

        var mode = endpoint.ApiKeyHeaderMode;
        if (mode == ApiKeyHeaderMode.Auto)
        {
            mode = endpoint.IsAzureEndpoint || endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? ApiKeyHeaderMode.ApiKeyHeader
                : ApiKeyHeaderMode.Bearer;
        }

        return mode == ApiKeyHeaderMode.ApiKeyHeader
            ? "api-key Header"
            : "Authorization: Bearer";
    }

    private static string DescribeTextProtocol(AiEndpoint endpoint)
    {
        if (endpoint.IsAzureEndpoint)
        {
            return "Azure deployments / chat/completions";
        }

        return EndpointProfileUrlBuilder.GetEffectiveTextProtocol(
            endpoint.ProfileId,
            endpoint.EndpointType,
            endpoint.TextApiProtocolMode,
            endpoint.IsAzureEndpoint) switch
        {
            TextApiProtocolMode.Responses => "responses",
            TextApiProtocolMode.ChatCompletionsRaw => "chat/completions",
            TextApiProtocolMode.ChatCompletionsV1 => "v1/chat/completions",
            _ => endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "responses（自动）"
                : "v1/chat/completions（自动）"
        };
    }

    private static string DescribeImageRoute(AiEndpoint endpoint)
    {
        if (endpoint.IsAzureEndpoint)
        {
            return "/openai/v1/images/*";
        }

        return endpoint.ImageApiRouteMode switch
        {
            ImageApiRouteMode.ImagesRaw => "/images/*",
            ImageApiRouteMode.V1Images => "/v1/images/*",
            _ => endpoint.EndpointType == EndpointApiType.ApiManagementGateway
                ? "自动（APIM：deployments → /v1/images → /images）"
                : "自动"
        };
    }

    private static IReadOnlyList<ApimImageProbePlan> BuildApimImageGenerateProbePlans(AiImageGenService service, AiConfig source)
    {
        var deploymentConfig = CloneConfigWithImageRouteMode(source, source.ImageApiRouteMode);
        var v1Config = CloneConfigWithImageRouteMode(source, ImageApiRouteMode.V1Images);
        var rawConfig = CloneConfigWithImageRouteMode(source, ImageApiRouteMode.ImagesRaw);

        return new List<ApimImageProbePlan>
        {
            new ApimImageProbePlan(
                "AOAI deployments 图片路由",
                deploymentConfig,
                service.GetApimDeploymentGenerateCandidateUrls(deploymentConfig)),
            new ApimImageProbePlan(
                "/v1/images/*",
                v1Config,
                service.GetGenerateCandidateUrlsForRoute(v1Config, ImageApiRouteMode.V1Images)),
            new ApimImageProbePlan(
                "/images/*",
                rawConfig,
                service.GetGenerateCandidateUrlsForRoute(rawConfig, ImageApiRouteMode.ImagesRaw))
        }
        .Where(plan => plan.CandidateUrls.Count > 0)
        .ToList();
    }

    private static AiConfig CloneConfigWithImageRouteMode(AiConfig source, ImageApiRouteMode routeMode)
    {
        return new AiConfig
        {
            ProfileId = source.ProfileId,
            EndpointType = source.EndpointType,
            ProviderType = source.ProviderType,
            ApiEndpoint = source.ApiEndpoint,
            ApiKey = source.ApiKey,
            ModelName = source.ModelName,
            DeploymentName = source.DeploymentName,
            ApiVersion = source.ApiVersion,
            AzureAuthMode = source.AzureAuthMode,
            ApiKeyHeaderMode = source.ApiKeyHeaderMode,
            TextApiProtocolMode = source.TextApiProtocolMode,
            ImageApiRouteMode = routeMode,
            AzureTenantId = source.AzureTenantId,
            AzureClientId = source.AzureClientId,
            SummaryEnableReasoning = source.SummaryEnableReasoning
        };
    }

    private static string BuildApimImageRouteProbeDetails(IReadOnlyList<AiImageGenService.ImageRouteProbeResult> results)
    {
        var builder = new StringBuilder();

        foreach (var result in results)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("----------------");
            }

            builder.AppendLine($"路由：{result.RouteLabel}");
            builder.AppendLine($"结果：{(result.IsSuccess ? "成功" : "失败")}");
            if (!string.IsNullOrWhiteSpace(result.SuccessfulUrl))
            {
                builder.AppendLine($"命中 URL：{result.SuccessfulUrl}");
            }

            if (result.StatusCode is not null)
            {
                builder.AppendLine($"HTTP：{result.StatusCode} {result.ReasonPhrase}");
            }

            builder.AppendLine("尝试 URL：");
            foreach (var url in result.AttemptedUrls)
            {
                builder.AppendLine($"- {url}");
            }

            if (result.IsSuccess)
            {
                builder.AppendLine($"返回图片数：{result.ImageCount}");
            }
            else if (!string.IsNullOrWhiteSpace(result.ErrorText))
            {
                builder.AppendLine($"错误：{result.ErrorText}");
            }
        }

        return builder.ToString().Trim();
    }

    private sealed record ApimImageProbePlan(
        string RouteLabel,
        AiConfig Config,
        IReadOnlyList<string> CandidateUrls);

    private static bool IsVideoFailureStatus(string status)
    {
        var normalized = status?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "failed" or "error" or "cancelled" or "canceled";
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

            if (current is HttpRequestException)
            {
                break;
            }

            current = current.InnerException;
            depth++;
        }

        return builder.ToString().Trim();
    }
}
