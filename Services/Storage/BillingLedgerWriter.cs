using System;
using Microsoft.Data.Sqlite;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.Storage
{
    /// <summary>
    /// billing_ledger WORM 写入器。每次 API 调用完成后写入一条审计记录。
    /// 使用单连接 + 事务确保原子性。
    /// </summary>
    public sealed class BillingLedgerWriter
    {
        private readonly ISqliteDbService _db;

        public BillingLedgerWriter(ISqliteDbService db) => _db = db;

        /// <summary>
        /// 插入一条 billing_ledger 记录。WORM：仅 INSERT，无 UPDATE/DELETE。
        /// </summary>
        public void Insert(BillingLedgerEntry entry)
        {
            using var conn = _db.CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
INSERT INTO billing_ledger (
    ledger_id, session_id, task_id, message_id,
    event_time, response_time, recorded_at,
    event_type, model_id, model_snapshot, api_endpoint, endpoint_profile,
    request_quality, request_size, request_n, request_format,
    has_reference_input, has_mask, partial_images, prompt_fingerprint,
    video_duration_seconds, video_resolution,
    estimated_output_tokens,
    actual_input_tokens, actual_output_tokens,
    actual_image_input_tokens, actual_image_output_tokens, actual_cached_tokens,
    actual_width, actual_height, actual_pixel_area,
    billing_tier_width, billing_tier_height, billing_tier_tokens, billing_unit_cost_usd,
    unit_price_input_per_m, unit_price_output_per_m, calculated_cost_usd,
    multiplier, base_unit_price, multiplier_cost,
    result_count, result_total_bytes, http_status, error_code,
    billing_config_version, record_checksum
) VALUES (
    @ledgerId, @sessionId, @taskId, @messageId,
    @eventTime, @responseTime, @recordedAt,
    @eventType, @modelId, @modelSnapshot, @apiEndpoint, @endpointProfile,
    @requestQuality, @requestSize, @requestN, @requestFormat,
    @hasReferenceInput, @hasMask, @partialImages, @promptFingerprint,
    @videoDurationSeconds, @videoResolution,
    @estimatedOutputTokens,
    @actualInputTokens, @actualOutputTokens,
    @actualImageInputTokens, @actualImageOutputTokens, @actualCachedTokens,
    @actualWidth, @actualHeight, @actualPixelArea,
    @billingTierWidth, @billingTierHeight, @billingTierTokens, @billingUnitCostUsd,
    @unitPriceInputPerM, @unitPriceOutputPerM, @calculatedCostUsd,
    @multiplier, @baseUnitPrice, @multiplierCost,
    @resultCount, @resultTotalBytes, @httpStatus, @errorCode,
    @billingConfigVersion, @recordChecksum
);";
                cmd.Parameters.AddWithValue("@ledgerId", entry.LedgerId);
                cmd.Parameters.AddWithValue("@sessionId", entry.SessionId);
                cmd.Parameters.AddWithValue("@taskId", (object?)entry.TaskId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@messageId", (object?)entry.MessageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@eventTime", entry.EventTime);
                cmd.Parameters.AddWithValue("@responseTime", (object?)entry.ResponseTime ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@recordedAt", entry.RecordedAt);
                cmd.Parameters.AddWithValue("@eventType", entry.EventType);
                cmd.Parameters.AddWithValue("@modelId", entry.ModelId);
                cmd.Parameters.AddWithValue("@modelSnapshot", (object?)entry.ModelSnapshot ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@apiEndpoint", entry.ApiEndpoint);
                cmd.Parameters.AddWithValue("@endpointProfile", (object?)entry.EndpointProfile ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requestQuality", (object?)entry.RequestQuality ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requestSize", (object?)entry.RequestSize ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requestN", entry.RequestN);
                cmd.Parameters.AddWithValue("@requestFormat", (object?)entry.RequestFormat ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hasReferenceInput", entry.HasReferenceInput ? 1 : 0);
                cmd.Parameters.AddWithValue("@hasMask", entry.HasMask ? 1 : 0);
                cmd.Parameters.AddWithValue("@partialImages", entry.PartialImages);
                cmd.Parameters.AddWithValue("@promptFingerprint", (object?)entry.PromptFingerprint ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@videoDurationSeconds", (object?)entry.VideoDurationSeconds ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@videoResolution", (object?)entry.VideoResolution ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@estimatedOutputTokens", (object?)entry.EstimatedOutputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualInputTokens", (object?)entry.ActualInputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualOutputTokens", (object?)entry.ActualOutputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualImageInputTokens", (object?)entry.ActualImageInputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualImageOutputTokens", (object?)entry.ActualImageOutputTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualCachedTokens", (object?)entry.ActualCachedTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualWidth", (object?)entry.ActualWidth ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualHeight", (object?)entry.ActualHeight ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@actualPixelArea", (object?)entry.ActualPixelArea ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@billingTierWidth", (object?)entry.BillingTierWidth ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@billingTierHeight", (object?)entry.BillingTierHeight ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@billingTierTokens", (object?)entry.BillingTierTokens ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@billingUnitCostUsd", (object?)entry.BillingUnitCostUsd ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@unitPriceInputPerM", (object?)entry.UnitPriceInputPerM ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@unitPriceOutputPerM", (object?)entry.UnitPriceOutputPerM ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@calculatedCostUsd", (object?)entry.CalculatedCostUsd ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@multiplier", (object?)entry.Multiplier ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@baseUnitPrice", (object?)entry.BaseUnitPrice ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@multiplierCost", (object?)entry.MultiplierCost ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@resultCount", entry.ResultCount);
                cmd.Parameters.AddWithValue("@resultTotalBytes", (object?)entry.ResultTotalBytes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@httpStatus", (object?)entry.HttpStatus ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@errorCode", (object?)entry.ErrorCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@billingConfigVersion", (object?)entry.BillingConfigVersion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@recordChecksum", entry.RecordChecksum);

                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}
