using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace TrueFluentPro.Services.Cloud
{
    /// <summary>
    /// 基于 MSAL.NET 的 Cloud 模式 AAD 认证服务。
    /// 独立于现有的 AzureTokenProvider（后者用于 Azure Cognitive Services AAD 认证）。
    /// 本服务专门用于对 TrueFluentPro SaaS 后端的 JWT 认证。
    /// </summary>
    public sealed class CloudAuthService : ICloudAuthService
    {
        private IPublicClientApplication? _msalApp;
        private string _tenantId = "";
        private string _clientId = "";
        private string _scope = "";

        public bool IsLoggedIn { get; private set; }
        public string? DisplayName { get; private set; }
        public string? UserId { get; private set; }

        public void Reconfigure(string tenantId, string clientId, string scope)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
            {
                _msalApp = null;
                return;
            }

            _tenantId = tenantId.Trim();
            _clientId = clientId.Trim();
            _scope = string.IsNullOrWhiteSpace(scope)
                ? $"api://{_clientId}/access"  // Standard Azure AD app scope format: api://{clientId}/access
                : scope.Trim();

            _msalApp = PublicClientApplicationBuilder
                .Create(_clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, _tenantId)
                .WithRedirectUri("http://localhost")
                .Build();

            Debug.WriteLine($"[CloudAuth] MSAL reconfigured: tenant={_tenantId}, client={_clientId}, scope={_scope}");
        }

        public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
        {
            if (_msalApp == null)
            {
                Debug.WriteLine("[CloudAuth] LoginAsync failed: MSAL not configured");
                return false;
            }

            try
            {
                // 先尝试静默获取
                var accounts = await _msalApp.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();

                AuthenticationResult result;
                if (firstAccount != null)
                {
                    try
                    {
                        result = await _msalApp.AcquireTokenSilent(new[] { _scope }, firstAccount)
                            .ExecuteAsync(cancellationToken);
                        ApplyAuthResult(result);
                        return true;
                    }
                    catch (MsalUiRequiredException)
                    {
                        // 需要交互式登录
                    }
                }

                // 交互式登录（弹出系统浏览器）
                result = await _msalApp.AcquireTokenInteractive(new[] { _scope })
                    .WithUseEmbeddedWebView(false)
                    .ExecuteAsync(cancellationToken);

                ApplyAuthResult(result);
                return true;
            }
            catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
            {
                Debug.WriteLine("[CloudAuth] User canceled login");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudAuth] LoginAsync error: {ex.Message}");
                return false;
            }
        }

        public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (_msalApp == null)
            {
                return null;
            }

            try
            {
                var accounts = await _msalApp.GetAccountsAsync();
                var firstAccount = accounts.FirstOrDefault();
                if (firstAccount == null)
                {
                    return null;
                }

                var result = await _msalApp.AcquireTokenSilent(new[] { _scope }, firstAccount)
                    .ExecuteAsync(cancellationToken);
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Token 过期且无法静默刷新
                Debug.WriteLine("[CloudAuth] Token expired, interactive login required");
                IsLoggedIn = false;
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CloudAuth] GetAccessTokenAsync error: {ex.Message}");
                return null;
            }
        }

        public async Task LogoutAsync()
        {
            if (_msalApp != null)
            {
                try
                {
                    var accounts = await _msalApp.GetAccountsAsync();
                    foreach (var account in accounts)
                    {
                        await _msalApp.RemoveAsync(account);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CloudAuth] LogoutAsync error: {ex.Message}");
                }
            }

            IsLoggedIn = false;
            DisplayName = null;
            UserId = null;
        }

        private void ApplyAuthResult(AuthenticationResult result)
        {
            IsLoggedIn = true;
            DisplayName = result.Account?.Username;
            UserId = result.UniqueId;
            Debug.WriteLine($"[CloudAuth] Logged in as {DisplayName} (oid={UserId})");
        }
    }
}
