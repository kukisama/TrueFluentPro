using System.Text.Json.Serialization;

namespace TrueFluentPro.Models.Cloud
{
    /// <summary>
    /// Cloud 模式相关的持久化设置（序列化到配置文件中）。
    /// </summary>
    public sealed class CloudSettings
    {
        /// <summary>当前服务模式（默认 SelfHosted，不影响现有用户）</summary>
        public ServiceMode Mode { get; set; } = ServiceMode.SelfHosted;

        /// <summary>SaaS 后端 API 地址，如 https://api.yourdomain.com</summary>
        public string BackendUrl { get; set; } = "";

        /// <summary>AAD 租户 ID（用于 MSAL 登录）</summary>
        public string AadTenantId { get; set; } = "";

        /// <summary>AAD 客户端应用 ID（public client）</summary>
        public string AadClientId { get; set; } = "";

        /// <summary>后端 API 的 scope，如 api://truefluentpro/access</summary>
        public string AadScope { get; set; } = "";
    }
}
