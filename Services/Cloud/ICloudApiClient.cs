using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models.Cloud;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// Cloud 模式下访问 SaaS 后端 API 的客户端接口。
    /// 所有请求自动附加 JWT Bearer token。
    /// </summary>
    public interface ICloudApiClient
    {
        /// <summary>后端是否可用（最近一次健康检查结果）</summary>
        bool IsBackendAvailable { get; }

        /// <summary>检查后端健康状态</summary>
        Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);

        /// <summary>获取当前用户 profile + 配额信息</summary>
        Task<CloudUserProfile?> GetUserProfileAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// 发送已认证的请求到后端 API（通用方法）。
        /// 自动附加 Bearer token，如果 token 过期会尝试刷新。
        /// </summary>
        Task<HttpResponseMessage> SendAuthenticatedAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 发送 POST 请求到后端代理端点，以流式方式返回响应流。
        /// 适用于 SSE chat completions 等流式 API。
        /// </summary>
        Task<Stream?> PostStreamAsync(
            string relativePath,
            object requestBody,
            CancellationToken cancellationToken = default);
    }
}
