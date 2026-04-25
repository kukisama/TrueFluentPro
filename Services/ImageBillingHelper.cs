using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TrueFluentPro.Models;
using TrueFluentPro.Services.Storage;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 图片生成计费与 staging 生命周期辅助。
    /// 由 ViewModel 在图片生成各阶段调用，封装 billing_ledger + task_staging 写入逻辑。
    /// </summary>
    public sealed class ImageBillingHelper
    {
        private readonly BillingLedgerWriter _ledgerWriter;
        private readonly TaskStagingWriter _stagingWriter;
        private readonly BillingTiersService _billingTiers;

        public ImageBillingHelper(
            BillingLedgerWriter ledgerWriter,
            TaskStagingWriter stagingWriter,
            BillingTiersService billingTiers)
        {
            _ledgerWriter = ledgerWriter;
            _stagingWriter = stagingWriter;
            _billingTiers = billingTiers;
        }

        // ── task_staging 生命周期 ──

        public void StagingInsert(string taskId, string sessionId, string taskType, string prompt)
        {
            try
            {
                _stagingWriter.Insert(new TaskStagingEntry
                {
                    TaskId = taskId,
                    SessionId = sessionId,
                    TaskType = taskType,
                    Status = TaskStagingStatus.Pending,
                    Prompt = prompt,
                    CreatedAt = DateTime.UtcNow.ToString("o")
                });
            }
            catch { /* staging 失败不阻断主流程 */ }
        }

        public void StagingRunning(string taskId)
        {
            try { _stagingWriter.UpdateStatus(taskId, TaskStagingStatus.Running); }
            catch { }
        }

        public void StagingLanded(string taskId, string? resultFilePath = null, long? fileSize = null,
            int? inputTokens = null, int? outputTokens = null, double? costUsd = null)
        {
            try
            {
                _stagingWriter.UpdateStatus(taskId, TaskStagingStatus.Landed,
                    resultFilePath: resultFilePath, fileSize: fileSize,
                    inputTokens: inputTokens, outputTokens: outputTokens, estimatedCostUsd: costUsd);
            }
            catch { }
        }

        public void StagingCommitted(string taskId)
        {
            try { _stagingWriter.UpdateStatus(taskId, TaskStagingStatus.Committed); }
            catch { }
        }

        public void StagingFailed(string taskId, string errorMessage, string? errorCode = null)
        {
            try
            {
                _stagingWriter.UpdateStatus(taskId, TaskStagingStatus.Failed,
                    errorCode: errorCode, errorMessage: errorMessage);
            }
            catch { }
        }

        public void StagingCancelled(string taskId)
        {
            try { _stagingWriter.UpdateStatus(taskId, TaskStagingStatus.Cancelled); }
            catch { }
        }

        // ── billing_ledger 写入 ──

        /// <summary>
        /// 图片生成成功后写入 billing_ledger 记录。
        /// </summary>
        public void RecordImageBilling(
            string sessionId,
            string taskId,
            string modelId,
            string apiEndpoint,
            string quality,
            string size,
            int requestN,
            string format,
            bool hasReferenceInput,
            int resultCount,
            long? resultTotalBytes,
            double generateSeconds,
            int? estimatedOutputTokens = null,
            int? actualInputTokens = null,
            int? actualOutputTokens = null,
            int? actualImageInputTokens = null,
            int? actualImageOutputTokens = null,
            int? actualCachedTokens = null,
            int? actualWidth = null,
            int? actualHeight = null)
        {
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                var ledgerId = Helpers.UlidGenerator.NewUlid();

                // Snap-Up 计费归档
                ParseSize(size, out var w, out var h);
                // 优先用实际生成图片尺寸，fallback 到请求尺寸
                var billingW = actualWidth ?? w;
                var billingH = actualHeight ?? h;
                var tier = _billingTiers.Config.Models.Count > 0
                    ? _billingTiers.SnapUp(modelId, billingW, billingH, quality)
                    : null;

                var entry = new BillingLedgerEntry
                {
                    LedgerId = ledgerId,
                    SessionId = sessionId,
                    TaskId = taskId,
                    EventTime = now,
                    RecordedAt = now,
                    EventType = hasReferenceInput ? "image_edit" : "image_generate",
                    ModelId = modelId,
                    ApiEndpoint = apiEndpoint,
                    RequestQuality = quality,
                    RequestSize = size,
                    RequestN = requestN,
                    RequestFormat = format,
                    HasReferenceInput = hasReferenceInput,
                    ResultCount = resultCount,
                    ResultTotalBytes = resultTotalBytes,
                    EstimatedOutputTokens = estimatedOutputTokens,
                    ActualInputTokens = actualInputTokens,
                    ActualOutputTokens = actualOutputTokens,
                    ActualImageInputTokens = actualImageInputTokens,
                    ActualImageOutputTokens = actualImageOutputTokens,
                    ActualCachedTokens = actualCachedTokens,
                    ActualWidth = actualWidth,
                    ActualHeight = actualHeight,
                    ActualPixelArea = billingW > 0 && billingH > 0 ? billingW * billingH : null,
                    BillingTierWidth = tier?.Width,
                    BillingTierHeight = tier?.Height,
                    BillingTierTokens = tier?.Tokens,
                    CalculatedCostUsd = tier != null
                        ? CalculateCost(modelId, actualOutputTokens ?? estimatedOutputTokens ?? 0, tier)
                        : null,
                    HttpStatus = 200,
                    RecordChecksum = "" // placeholder, computed below
                };

                // 计算校验和
                var checksum = ComputeChecksum(entry);
                // 因为 BillingLedgerEntry 是 init-only，需要重新构建
                entry = CloneWithChecksum(entry, checksum);

                _ledgerWriter.Insert(entry);
            }
            catch { /* billing 失败不阻断主流程 */ }
        }

        /// <summary>
        /// 图片生成失败后写入 billing_ledger 记录（记录错误但无计费）。
        /// </summary>
        public void RecordImageBillingError(
            string sessionId,
            string taskId,
            string modelId,
            string apiEndpoint,
            string quality,
            string size,
            int requestN,
            bool hasReferenceInput,
            int httpStatus,
            string errorCode)
        {
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                var ledgerId = Helpers.UlidGenerator.NewUlid();

                var entry = new BillingLedgerEntry
                {
                    LedgerId = ledgerId,
                    SessionId = sessionId,
                    TaskId = taskId,
                    EventTime = now,
                    RecordedAt = now,
                    EventType = hasReferenceInput ? "image_edit" : "image_generate",
                    ModelId = modelId,
                    ApiEndpoint = apiEndpoint,
                    RequestQuality = quality,
                    RequestSize = size,
                    RequestN = requestN,
                    HasReferenceInput = hasReferenceInput,
                    ResultCount = 0,
                    HttpStatus = httpStatus,
                    ErrorCode = errorCode,
                    RecordChecksum = ""
                };

                entry = CloneWithChecksum(entry, ComputeChecksum(entry));
                _ledgerWriter.Insert(entry);
            }
            catch { }
        }

        private static void ParseSize(string? size, out int w, out int h)
        {
            w = 1024; h = 1024;
            if (string.IsNullOrWhiteSpace(size) || size == "auto") return;
            var parts = size.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph))
            {
                w = pw; h = ph;
            }
        }

        private double? CalculateCost(string modelId, int outputTokens, BillingTier tier)
        {
            if (tier.PriceUsd > 0)
            {
                // 固定计费模型（gpt-image-1.5 等有 priceUsd 的档位）
                return tier.PriceUsd;
            }
            if (tier.Tokens > 0)
            {
                // token 计费模型（gpt-image-2）：使用模型级 pricePerMillionOutputTokens
                var pricePerM = _billingTiers.GetOutputTokenPrice(modelId);
                return (double)outputTokens * pricePerM / 1_000_000.0;
            }
            return null;
        }

        private static string ComputeChecksum(BillingLedgerEntry e)
        {
            var payload = $"{e.LedgerId}|{e.SessionId}|{e.EventTime}|{e.EventType}|{e.ModelId}|{e.ResultCount}|{e.CalculatedCostUsd}|{e.ActualInputTokens}|{e.ActualOutputTokens}|{e.ActualImageInputTokens}|{e.ActualImageOutputTokens}|{e.ActualCachedTokens}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexStringLower(hash)[..16];
        }

        private static BillingLedgerEntry CloneWithChecksum(BillingLedgerEntry src, string checksum)
        {
            return new BillingLedgerEntry
            {
                LedgerId = src.LedgerId,
                SessionId = src.SessionId,
                TaskId = src.TaskId,
                MessageId = src.MessageId,
                EventTime = src.EventTime,
                ResponseTime = src.ResponseTime,
                RecordedAt = src.RecordedAt,
                EventType = src.EventType,
                ModelId = src.ModelId,
                ModelSnapshot = src.ModelSnapshot,
                ApiEndpoint = src.ApiEndpoint,
                EndpointProfile = src.EndpointProfile,
                RequestQuality = src.RequestQuality,
                RequestSize = src.RequestSize,
                RequestN = src.RequestN,
                RequestFormat = src.RequestFormat,
                HasReferenceInput = src.HasReferenceInput,
                HasMask = src.HasMask,
                PartialImages = src.PartialImages,
                PromptFingerprint = src.PromptFingerprint,
                VideoDurationSeconds = src.VideoDurationSeconds,
                VideoResolution = src.VideoResolution,
                EstimatedOutputTokens = src.EstimatedOutputTokens,
                ActualInputTokens = src.ActualInputTokens,
                ActualOutputTokens = src.ActualOutputTokens,
                ActualImageInputTokens = src.ActualImageInputTokens,
                ActualImageOutputTokens = src.ActualImageOutputTokens,
                ActualCachedTokens = src.ActualCachedTokens,
                ActualWidth = src.ActualWidth,
                ActualHeight = src.ActualHeight,
                ActualPixelArea = src.ActualPixelArea,
                BillingTierWidth = src.BillingTierWidth,
                BillingTierHeight = src.BillingTierHeight,
                BillingTierTokens = src.BillingTierTokens,
                BillingUnitCostUsd = src.BillingUnitCostUsd,
                UnitPriceInputPerM = src.UnitPriceInputPerM,
                UnitPriceOutputPerM = src.UnitPriceOutputPerM,
                CalculatedCostUsd = src.CalculatedCostUsd,
                Multiplier = src.Multiplier,
                BaseUnitPrice = src.BaseUnitPrice,
                MultiplierCost = src.MultiplierCost,
                ResultCount = src.ResultCount,
                ResultTotalBytes = src.ResultTotalBytes,
                HttpStatus = src.HttpStatus,
                ErrorCode = src.ErrorCode,
                BillingConfigVersion = src.BillingConfigVersion,
                RecordChecksum = checksum
            };
        }
    }
}
