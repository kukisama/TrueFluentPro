using System.Text.Json;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Middleware;

/// <summary>
/// Checks per-user monthly quota before allowing proxied requests.
/// Returns 429 when quota is exceeded.
/// </summary>
public sealed class QuotaMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<QuotaMiddleware> _logger;

    // Endpoint path prefix → resource type mapping
    private static readonly Dictionary<string, string> EndpointResourceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/api/v1/chat/"] = "chat_token",
        ["/api/v1/images/"] = "image",
        ["/api/v1/videos/"] = "video_second",
        ["/api/v1/speech/transcribe"] = "speech_minute",
        ["/api/v1/speech/synthesize"] = "speech_minute",
        ["/api/v1/storage/"] = "speech_minute",  // SAS upload is part of speech workflow
    };

    // Endpoints exempt from quota checking
    private static readonly string[] ExemptPrefixes =
    [
        "/health",
        "/api/v1/user/",
        "/api/v1/admin/",
    ];

    public QuotaMiddleware(RequestDelegate next, ILogger<QuotaMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IQuotaCache quotaCache, IApiDbService db)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Skip exempt endpoints
        if (IsExempt(path))
        {
            await _next(context);
            return;
        }

        // Only check authenticated requests
        var userId = context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? context.User.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            await _next(context);
            return;
        }

        var resourceType = MapEndpointToResource(path);
        if (resourceType == null)
        {
            await _next(context);
            return;
        }

        // Get user's plan and limits
        var user = await db.GetUserAsync(userId);
        var plan = user != null ? await db.GetPlanAsync(user.Subscription) : null;
        var limit = plan?.Limits.GetValueOrDefault(resourceType) ?? 0;

        if (limit <= 0)
        {
            // No limit configured → allow (or deny if plan has 0 explicitly)
            if (plan?.Limits.ContainsKey(resourceType) == true && limit == 0)
            {
                await WriteQuotaExceededResponse(context, resourceType, 0, 0);
                return;
            }
            await _next(context);
            return;
        }

        var currentUsage = await quotaCache.GetCurrentUsageAsync(userId, resourceType);
        if (currentUsage >= limit)
        {
            _logger.LogWarning("Quota exceeded for user {UserId}, resource {Resource}: {Used}/{Limit}",
                userId, resourceType, currentUsage, limit);
            await WriteQuotaExceededResponse(context, resourceType, currentUsage, limit);
            return;
        }

        // Add quota headers to response
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Quota-Remaining"] = Math.Max(0, limit - currentUsage).ToString();
            context.Response.Headers["X-Quota-Limit"] = limit.ToString();
            return Task.CompletedTask;
        });

        await _next(context);
    }

    private static bool IsExempt(string path)
    {
        foreach (var prefix in ExemptPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? MapEndpointToResource(string path)
    {
        foreach (var (prefix, resource) in EndpointResourceMap)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return resource;
        }
        return null;
    }

    private static async Task WriteQuotaExceededResponse(HttpContext context, string resourceType, long usage, long limit)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";

        // Set Retry-After to beginning of next month
        var now = DateTime.UtcNow;
        var nextMonth = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        var retryAfterSeconds = (int)(nextMonth - now).TotalSeconds;
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();

        await context.Response.WriteAsJsonAsync(new
        {
            error = "quota_exceeded",
            message = $"Monthly {resourceType} quota exceeded",
            usage,
            limit,
            resets_at = nextMonth.ToString("o")
        });
    }
}
