namespace TrueFluentPro.Api.Services;

/// <summary>
/// Three-level credential lookup: Environment → DB → Key Vault.
/// </summary>
public interface ICredentialProvider
{
    Task<string?> GetCredentialAsync(string key);
}

public sealed class CredentialProvider : ICredentialProvider
{
    private readonly IApiDbService _db;
    private readonly ILogger<CredentialProvider> _logger;

    public CredentialProvider(IApiDbService db, ILogger<CredentialProvider> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string?> GetCredentialAsync(string key)
    {
        // 1. Environment variable (highest priority)
        var envKey = key.ToUpperInvariant().Replace('.', '_').Replace('-', '_');
        var envValue = Environment.GetEnvironmentVariable(envKey);
        if (!string.IsNullOrEmpty(envValue))
            return envValue;

        // 2. Database (encrypted)
        try
        {
            var dbValue = await _db.GetDecryptedCredentialAsync(key);
            if (!string.IsNullOrEmpty(dbValue))
                return dbValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read credential '{Key}' from database", key);
        }

        // 3. Key Vault (future — not implemented yet)

        return null;
    }
}
