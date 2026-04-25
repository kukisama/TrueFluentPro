namespace TrueFluentPro.Models
{
    /// <summary>
    /// 图片/媒体生成错误码标准分类。
    /// 用于 task_staging.error_code 和 billing_ledger.error_code。
    /// </summary>
    public static class ImageErrorCodes
    {
        public const string RateLimit = "rate_limit";
        public const string ContentFilter = "content_filter";
        public const string Timeout = "timeout";
        public const string NetworkError = "network";
        public const string ApiError = "api_error";
        public const string UserCancel = "user_cancel";
        public const string QuotaExceeded = "quota_exceeded";
        public const string ModelNotFound = "model_not_found";
        public const string InvalidInput = "invalid_input";

        /// <summary>
        /// 从 HTTP 状态码和错误文本推断 error_code。
        /// </summary>
        public static string Classify(int? httpStatus, string? errorText)
        {
            if (httpStatus == 429) return RateLimit;
            if (httpStatus == 408) return Timeout;

            if (!string.IsNullOrEmpty(errorText))
            {
                var lower = errorText.ToLowerInvariant();
                if (lower.Contains("content_filter") || lower.Contains("safety") || lower.Contains("content policy"))
                    return ContentFilter;
                if (lower.Contains("rate limit") || lower.Contains("too many requests"))
                    return RateLimit;
                if (lower.Contains("timeout") || lower.Contains("timed out"))
                    return Timeout;
                if (lower.Contains("model") && (lower.Contains("not found") || lower.Contains("does not exist")))
                    return ModelNotFound;
                if (lower.Contains("quota") || lower.Contains("exceeded"))
                    return QuotaExceeded;
                if (lower.Contains("invalid") && (lower.Contains("image") || lower.Contains("format") || lower.Contains("size")))
                    return InvalidInput;
            }

            if (httpStatus.HasValue && httpStatus >= 500) return ApiError;
            if (httpStatus.HasValue && httpStatus >= 400) return ApiError;

            return NetworkError;
        }

        /// <summary>
        /// 返回用户友好的错误描述。
        /// </summary>
        public static string GetFriendlyMessage(string errorCode) => errorCode switch
        {
            RateLimit => "请求频率过高，请稍后重试",
            ContentFilter => "内容安全策略拦截，请修改提示词",
            Timeout => "请求超时，建议降低 quality 或缩小尺寸",
            NetworkError => "网络连接失败，请检查网络",
            ApiError => "API 服务端错误，请稍后重试",
            UserCancel => "用户主动取消",
            QuotaExceeded => "配额已用尽",
            ModelNotFound => "模型部署不存在，请检查配置",
            InvalidInput => "输入参数不合法，请检查图片格式或尺寸",
            _ => "未知错误"
        };
    }
}
