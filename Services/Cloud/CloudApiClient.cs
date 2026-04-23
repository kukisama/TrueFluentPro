using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models.Cloud;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// Cloud 模式下访问 SaaS 后端 API 的 HttpClient 封装。
    /// 自动从 ICloudAuthService 获取 JWT token 并附加到请求头。
    /// </summary>
    public sealed class CloudApiClient : ICloudApiClient
    {
        private readonly ICloudAuthService _authService;
        private readonly HttpClient _httpClient;
        private readonly IServiceModeManager _modeManager;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        public bool IsBackendAvailable { get; private set; }

        public CloudApiClient(ICloudAuthService authService, IServiceModeManager modeManager)
        {
            _authService = authService;
            _modeManager = modeManager;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(120)
            };
        }

        public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            var backendUrl = _modeManager.BackendUrl;
            if (string.IsNullOrWhiteSpace(backendUrl))
            {
                IsBackendAvailable = false;
                return false;
            }

            try
            {
                var response = await _httpClient.GetAsync(
                    $"{backendUrl.TrimEnd('/')}/health",
                    cancellationToken);
                IsBackendAvailable = response.IsSuccessStatusCode;
                return IsBackendAvailable;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudApi] Health check failed: {ex.Message}");
                IsBackendAvailable = false;
                return false;
            }
        }

        public async Task<CloudUserProfile?> GetUserProfileAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl("/api/v1/user/profile"));
                var response = await SendAuthenticatedAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[CloudApi] GetUserProfile failed: {response.StatusCode}");
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<CloudUserProfile>(_jsonOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudApi] GetUserProfile error: {ex.Message}");
                return null;
            }
        }

        public async Task<HttpResponseMessage> SendAuthenticatedAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            var token = await _authService.GetAccessTokenAsync(cancellationToken);
            if (token != null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        public async Task<Stream?> PostStreamAsync(
            string relativePath,
            object requestBody,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(relativePath))
                {
                    Content = JsonContent.Create(requestBody, options: _jsonOptions)
                };

                var response = await SendAuthenticatedAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"[CloudApi] PostStream {relativePath} failed: {response.StatusCode}");
                    return null;
                }

                return await response.Content.ReadAsStreamAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudApi] PostStream {relativePath} error: {ex.Message}");
                return null;
            }
        }

        private string BuildUrl(string relativePath)
        {
            var baseUrl = _modeManager.BackendUrl?.TrimEnd('/') ?? "";
            return $"{baseUrl}{relativePath}";
        }
    }
}
