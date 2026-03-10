using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services
{
    public interface IAzureTokenProviderStore
    {
        AzureTokenProvider GetProvider(string? profileKey = null);

        Task<AzureTokenProvider?> GetAuthenticatedProviderAsync(
            string? profileKey,
            string? tenantId,
            string? clientId,
            CancellationToken cancellationToken = default);
    }

    public sealed class AzureTokenProviderStore : IAzureTokenProviderStore
    {
        private readonly ConcurrentDictionary<string, AzureTokenProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _providerLocks = new(StringComparer.OrdinalIgnoreCase);

        public AzureTokenProvider GetProvider(string? profileKey = null)
        {
            var normalizedKey = NormalizeProfileKey(profileKey);
            return _providers.GetOrAdd(normalizedKey, key => new AzureTokenProvider(key));
        }

        public async Task<AzureTokenProvider?> GetAuthenticatedProviderAsync(
            string? profileKey,
            string? tenantId,
            string? clientId,
            CancellationToken cancellationToken = default)
        {
            var normalizedKey = NormalizeProfileKey(profileKey);
            var provider = GetProvider(normalizedKey);
            var gate = _providerLocks.GetOrAdd(normalizedKey, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (await TryRefreshExistingTokenAsync(provider, cancellationToken))
                {
                    return provider;
                }

                var silentOk = await provider.TrySilentLoginAsync(tenantId, clientId, cancellationToken);
                if (!silentOk)
                {
                    return null;
                }

                return await TryRefreshExistingTokenAsync(provider, cancellationToken)
                    ? provider
                    : null;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<bool> TryRefreshExistingTokenAsync(AzureTokenProvider provider, CancellationToken cancellationToken)
        {
            if (!provider.IsLoggedIn)
            {
                return false;
            }

            try
            {
                await provider.GetTokenAsync(cancellationToken);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeProfileKey(string? profileKey)
            => string.IsNullOrWhiteSpace(profileKey)
                ? "shared"
                : profileKey.Trim();
    }
}
