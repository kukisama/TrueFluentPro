using System;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 听析中心 STT/TTS 路由审计日志。只记录路线、资源名、URL 等非敏感信息，禁止记录 token/key。
    /// </summary>
    public static class AudioLabRouteAuditLog
    {
        public static void Info(string message)
            => Write(message, isSuccess: true);

        public static void Failure(string message)
            => Write(message, isSuccess: false);

        private static void Write(string message, bool isSuccess)
        {
            try
            {
                if (!AppLogService.IsInitialized)
                {
                    return;
                }

                AppLogService.Instance.LogAudit("听析路由", $"[听析路由] {message}", isSuccess);
            }
            catch
            {
                // 路由审计失败不影响 STT/TTS 主流程。
            }
        }

        public static string Safe(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? "<empty>"
                : value.Replace("'", "’", StringComparison.Ordinal).Replace(Environment.NewLine, " ", StringComparison.Ordinal);
    }
}