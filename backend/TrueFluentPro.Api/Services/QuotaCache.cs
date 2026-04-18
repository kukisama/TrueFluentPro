namespace TrueFluentPro.Api.Services;

/// <summary>
/// Quota cache: tracks per-user monthly resource usage.
/// In-memory implementation for single-instance deployment.
/// </summary>
public interface IQuotaCache
{
    Task<long> IncrementUsageAsync(string userId, string resourceType, long amount);
    Task<long> GetCurrentUsageAsync(string userId, string resourceType);
    Task ResetUsageAsync(string userId, string resourceType);
}

public sealed class InMemoryQuotaCache : IQuotaCache
{
    private readonly ConcurrentDictionary<string, long> _cache = new();
    private readonly IApiDbService _db;
    private readonly ILogger<InMemoryQuotaCache> _logger;
    private readonly Lock _monthLock = new();
    private string _currentYearMonth = DateTime.UtcNow.ToString("yyyy-MM");

    public InMemoryQuotaCache(IApiDbService db, ILogger<InMemoryQuotaCache> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<long> IncrementUsageAsync(string userId, string resourceType, long amount)
    {
        CheckMonthRollover();
        var key = CacheKey(userId, resourceType);
        var newValue = _cache.AddOrUpdate(key, amount, (_, old) => old + amount);

        // Async write to DB (fire-and-forget for performance)
        _ = Task.Run(async () =>
        {
            try
            {
                await _db.UpsertMonthlyUsageAsync(userId, resourceType, _currentYearMonth, newValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist usage to DB for {UserId}/{ResourceType}", userId, resourceType);
            }
        });

        return newValue;
    }

    public async Task<long> GetCurrentUsageAsync(string userId, string resourceType)
    {
        CheckMonthRollover();
        var key = CacheKey(userId, resourceType);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        // Load from DB on cache miss
        var dbValue = await _db.GetMonthlyUsageAsync(userId, resourceType, _currentYearMonth);
        _cache.TryAdd(key, dbValue);
        return dbValue;
    }

    public Task ResetUsageAsync(string userId, string resourceType)
    {
        var key = CacheKey(userId, resourceType);
        _cache.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static string CacheKey(string userId, string resourceType)
        => $"{userId}:{resourceType}";

    private void CheckMonthRollover()
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM");
        if (now != _currentYearMonth)
        {
            lock (_monthLock)
            {
                // Double-check inside lock
                if (now != _currentYearMonth)
                {
                    _currentYearMonth = now;
                    _cache.Clear();
                }
            }
        }
    }
}
