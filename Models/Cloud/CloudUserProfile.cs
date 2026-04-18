using System.Collections.Generic;

namespace TrueFluentPro.Models.Cloud
{
    /// <summary>
    /// Cloud 模式下从后端 /api/v1/user/profile 获取的用户信息和配额。
    /// </summary>
    public sealed class CloudUserProfile
    {
        public string UserId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Subscription { get; set; } = "free";
        public bool IsAdmin { get; set; }
        public Dictionary<string, QuotaInfo> Quotas { get; set; } = new();
    }

    public sealed class QuotaInfo
    {
        public long Used { get; set; }
        public long Limit { get; set; }
        public long Remaining => Limit - Used;
    }
}
