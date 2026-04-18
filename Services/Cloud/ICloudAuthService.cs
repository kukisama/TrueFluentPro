using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// Cloud 模式 AAD 认证服务接口。
    /// 负责 MSAL.NET 登录/登出/Token 获取，完全解耦于现有的 AzureTokenProvider。
    /// </summary>
    public interface ICloudAuthService
    {
        /// <summary>当前是否已登录</summary>
        bool IsLoggedIn { get; }

        /// <summary>当前登录用户的显示名称（未登录时为 null）</summary>
        string? DisplayName { get; }

        /// <summary>当前登录用户的 AAD oid（未登录时为 null）</summary>
        string? UserId { get; }

        /// <summary>
        /// 交互式登录。首次调用会弹出系统浏览器进行 AAD 登录。
        /// </summary>
        Task<bool> LoginAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 获取有效的 access token。如果已缓存且未过期，静默返回；否则尝试静默刷新。
        /// 如果无法获取，返回 null（调用方应提示重新登录）。
        /// </summary>
        Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);

        /// <summary>登出并清除缓存的 token</summary>
        Task LogoutAsync();

        /// <summary>
        /// 使用新的 AAD 配置重新初始化 MSAL 客户端。
        /// 在用户更改 Cloud 设置中的 TenantId/ClientId/Scope 后调用。
        /// </summary>
        void Reconfigure(string tenantId, string clientId, string scope);
    }
}
