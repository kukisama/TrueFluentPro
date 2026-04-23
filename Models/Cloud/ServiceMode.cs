using System.Text.Json.Serialization;

namespace TrueFluentPro.Models.Cloud
{
    /// <summary>
    /// 应用服务模式：Self-Hosted（用户自带 key 直连 Azure）或 Cloud（走 SaaS 后端代理）。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ServiceMode
    {
        /// <summary>用户自带 key, 直连 Azure（现有默认行为）</summary>
        SelfHosted,

        /// <summary>通过 SaaS 后端代理访问 Azure 服务</summary>
        Cloud
    }
}
