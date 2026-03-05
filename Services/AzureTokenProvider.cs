using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// Azure Entra ID (AAD) Token 提供者。
    /// 仅在用户手动点击"登录"时触发认证，不自动弹窗。
    /// </summary>
    public class AzureTokenProvider
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private readonly string _profileKey;

        private TokenCredential? _credential;
        private readonly string _scope = "https://cognitiveservices.azure.com/.default";
        private readonly string _armScope = "https://management.azure.com/.default";
        private AccessToken _cachedToken;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private string AuthRecordPath =>
            Path.Combine(PathManager.Instance.AppDataPath, $"azure_auth_record_{_profileKey}.json");

        private string TokenCacheName => $"TrueFluentPro_{_profileKey}";

        public AzureTokenProvider(string? profileKey = null)
        {
            _profileKey = string.IsNullOrWhiteSpace(profileKey)
                ? "shared"
                : profileKey.Trim();
        }

        /// <summary>
        /// 当前是否已登录（持有有效 credential）
        /// </summary>
        public bool IsLoggedIn => _credential != null;

        /// <summary>
        /// 已登录的用户名（如果有）
        /// </summary>
        public string? Username { get; private set; }

        /// <summary>
        /// Token 过期时间
        /// </summary>
        public DateTimeOffset? TokenExpiry => _cachedToken.ExpiresOn == default ? null : _cachedToken.ExpiresOn;

        /// <summary>
        /// 使用设备代码流登录。onDeviceCode 回调用于在 UI 上显示设备码。
        /// </summary>
        public async Task<bool> LoginAsync(
            string? tenantId,
            string? clientId,
            Action<string>? onDeviceCode = null,
            CancellationToken ct = default)
        {
            try
            {
                // 尝试从已保存的 AuthenticationRecord 静默恢复
                AuthenticationRecord? record = null;
                if (File.Exists(AuthRecordPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(AuthRecordPath);
                        record = await AuthenticationRecord.DeserializeAsync(stream, ct);
                    }
                    catch
                    {
                        // 文件损坏，忽略
                        File.Delete(AuthRecordPath);
                    }
                }

                var options = new DeviceCodeCredentialOptions
                {
                    TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                    {
                        Name = TokenCacheName
                    },
                    DeviceCodeCallback = (code, cancellation) =>
                    {
                        onDeviceCode?.Invoke(code.Message);
                        return Task.CompletedTask;
                    }
                };

                if (!string.IsNullOrWhiteSpace(tenantId))
                    options.TenantId = tenantId;

                if (!string.IsNullOrWhiteSpace(clientId))
                    options.ClientId = clientId;

                if (record != null)
                    options.AuthenticationRecord = record;

                var credential = new DeviceCodeCredential(options);

                // 获取 Token（会触发设备代码流或从缓存恢复）
                _cachedToken = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _scope }), ct);

                _credential = credential;

                // 保存 AuthenticationRecord 便于后续静默恢复
                try
                {
                    var newRecord = await credential.AuthenticateAsync(
                        new TokenRequestContext(new[] { _scope }), ct);
                    using var stream = File.Create(AuthRecordPath);
                    await newRecord.SerializeAsync(stream, ct);
                    Username = newRecord.Username;
                }
                catch
                {
                    // 保存失败不影响登录结果
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AAD 登录失败: {ex.Message}");
                _credential = null;
                return false;
            }
        }

        /// <summary>
        /// 使用交互式浏览器登录（系统浏览器）。更适合多账号切换。
        /// </summary>
        public async Task<bool> LoginInteractiveAsync(
            string? tenantId,
            string? clientId,
            CancellationToken ct = default)
        {
            try
            {
                AuthenticationRecord? record = null;
                if (File.Exists(AuthRecordPath))
                {
                    try
                    {
                        using var stream = File.OpenRead(AuthRecordPath);
                        record = await AuthenticationRecord.DeserializeAsync(stream, ct);
                    }
                    catch
                    {
                        File.Delete(AuthRecordPath);
                    }
                }

                var options = new InteractiveBrowserCredentialOptions
                {
                    TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                    {
                        Name = TokenCacheName
                    }
                };

                if (!string.IsNullOrWhiteSpace(tenantId))
                    options.TenantId = tenantId;
                if (!string.IsNullOrWhiteSpace(clientId))
                    options.ClientId = clientId;
                if (record != null)
                    options.AuthenticationRecord = record;

                var credential = new InteractiveBrowserCredential(options);

                _cachedToken = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _scope }), ct);

                _credential = credential;

                try
                {
                    var newRecord = await credential.AuthenticateAsync(
                        new TokenRequestContext(new[] { _scope }), ct);
                    using var stream = File.Create(AuthRecordPath);
                    await newRecord.SerializeAsync(stream, ct);
                    Username = newRecord.Username;
                }
                catch
                {
                    // 保存失败不影响登录结果
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AAD 交互式登录失败: {ex.Message}");
                _credential = null;
                return false;
            }
        }

        /// <summary>
        /// 自动登录：优先静默恢复；若失败则优先交互式浏览器；最后回退设备代码流。
        /// </summary>
        public async Task<bool> LoginAutoAsync(
            string? tenantId,
            string? clientId,
            Action<string>? onDeviceCode = null,
            CancellationToken ct = default,
            bool forceInteractive = false)
        {
            if (!forceInteractive)
            {
                try
                {
                    if (await TrySilentLoginAsync(tenantId, clientId, ct))
                        return true;
                }
                catch
                {
                    // 静默失败不阻断
                }
            }

            // 交互式优先：更符合“少登录 + 易切换账号”的体验
            var interactiveOk = await LoginInteractiveAsync(tenantId, clientId, ct);
            if (interactiveOk)
                return true;

            // 最后回退设备代码
            return await LoginAsync(tenantId, clientId, onDeviceCode, ct);
        }

        /// <summary>
        /// 尝试使用已保存的凭据静默获取 Token（不弹窗）
        /// </summary>
        public async Task<bool> TrySilentLoginAsync(
            string? tenantId,
            string? clientId,
            CancellationToken ct = default)
        {
            if (!File.Exists(AuthRecordPath))
                return false;

            try
            {
                using var stream = File.OpenRead(AuthRecordPath);
                var record = await AuthenticationRecord.DeserializeAsync(stream, ct);

                var options = new DeviceCodeCredentialOptions
                {
                    AuthenticationRecord = record,
                    TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                    {
                        Name = TokenCacheName
                    },
                    // 静默模式下不希望弹窗，但 DeviceCodeCredential 需要回调
                    DeviceCodeCallback = (_, _) => Task.CompletedTask
                };

                if (!string.IsNullOrWhiteSpace(tenantId))
                    options.TenantId = tenantId;
                if (!string.IsNullOrWhiteSpace(clientId))
                    options.ClientId = clientId;

                var credential = new DeviceCodeCredential(options);

                // 尝试静默获取，限时 5 秒防止设备代码流挂起
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                _cachedToken = await credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _scope }), cts.Token);

                _credential = credential;
                Username = record.Username;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取当前有效的 Bearer Token
        /// </summary>
        public async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            if (_credential == null)
                throw new InvalidOperationException("未登录，请先调用 LoginAsync");

            // 若 Token 还未过期（提前 2 分钟刷新），直接返回
            if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                return _cachedToken.Token;

            await _lock.WaitAsync(ct);
            try
            {
                // 双重检查
                if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                    return _cachedToken.Token;

                _cachedToken = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _scope }), ct);

                return _cachedToken.Token;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// 获取当前账号可访问的租户列表（通过 ARM tenants API）。
        /// 需要先完成一次 LoginAsync/TrySilentLoginAsync。
        /// </summary>
        public async Task<IReadOnlyList<AzureTenantInfo>> GetAvailableTenantsAsync(CancellationToken ct = default)
        {
            if (_credential == null)
                throw new InvalidOperationException("未登录，请先调用 LoginAsync");

            try
            {
                var armToken = await _credential.GetTokenAsync(
                    new TokenRequestContext(new[] { _armScope }), ct);

                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "https://management.azure.com/tenants?api-version=2020-01-01");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", armToken.Token);

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"获取租户列表失败: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return Array.Empty<AzureTenantInfo>();
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("value", out var valueElem)
                    || valueElem.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<AzureTenantInfo>();
                }

                var list = new List<AzureTenantInfo>();
                foreach (var item in valueElem.EnumerateArray())
                {
                    var tid = item.TryGetProperty("tenantId", out var tidElem)
                        ? tidElem.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(tid))
                        continue;

                    var name = item.TryGetProperty("displayName", out var nameElem)
                        ? nameElem.GetString()
                        : null;

                    list.Add(new AzureTenantInfo
                    {
                        TenantId = tid.Trim(),
                        DisplayName = name?.Trim() ?? ""
                    });
                }

                return list;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取租户列表异常: {ex.Message}");
                return Array.Empty<AzureTenantInfo>();
            }
        }

        /// <summary>
        /// 注销，清除缓存的凭据、AuthRecord 配置文件及 MSAL 持久化 Token Cache。
        /// </summary>
        public void Logout()
        {
            // 1. 删除 AuthenticationRecord 配置文件
            TryDeleteFile(AuthRecordPath, "AuthRecord");

            // 2. 删除 MSAL 持久化 Token Cache 文件（Windows 位于 %LOCALAPPDATA%\.IdentityService\）
            try
            {
                var identityServiceDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ".IdentityService");

                if (Directory.Exists(identityServiceDir))
                {
                    // MSAL cache 文件名 = TokenCachePersistenceOptions.Name
                    var cachePath = Path.Combine(identityServiceDir, TokenCacheName);
                    TryDeleteFile(cachePath, "MSAL TokenCache");

                    // MSAL 可能同时生成 .lockfile
                    TryDeleteFile(cachePath + ".lockfile", "MSAL TokenCache lockfile");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理 MSAL Token Cache 目录时出错: {ex.Message}");
            }

            _credential = null;
            _cachedToken = default;
            Username = null;
        }

        /// <summary>
        /// 尝试删除指定文件，失败时输出调试日志。
        /// </summary>
        private static void TryDeleteFile(string path, string label)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除 {label} 失败 ({path}): {ex.Message}");
            }
        }
    }
}
