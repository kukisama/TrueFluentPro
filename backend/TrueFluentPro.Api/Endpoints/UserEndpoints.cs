using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P1: User profile and usage endpoints.
/// </summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/user").RequireAuthorization();

        group.MapGet("/profile", GetProfile);
        group.MapGet("/usage", GetUsageEvents);
        group.MapGet("/usage/summary", GetUsageSummary);
    }

    private static async Task<IResult> GetProfile(HttpContext ctx, IApiDbService db, IQuotaCache quotaCache)
    {
        var userId = GetUserId(ctx);

        // Auto-provision user on first access
        var user = await db.GetUserAsync(userId);
        if (user == null)
        {
            user = new UserRecord
            {
                Id = userId,
                DisplayName = ctx.User.FindFirst("name")?.Value
                           ?? ctx.User.FindFirst("preferred_username")?.Value
                           ?? "User",
                Email = ctx.User.FindFirst("email")?.Value
                     ?? ctx.User.FindFirst("preferred_username")?.Value,
                Subscription = "free"
            };
            await db.UpsertUserAsync(user);
        }

        // Build quota info
        var plan = await db.GetPlanAsync(user.Subscription);
        var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var monthlyUsage = await db.GetAllMonthlyUsageAsync(userId, yearMonth);

        var quotas = new Dictionary<string, QuotaInfo>();
        if (plan?.Limits != null)
        {
            foreach (var (resource, limit) in plan.Limits)
            {
                monthlyUsage.TryGetValue(resource, out var used);
                quotas[resource] = new QuotaInfo { Used = used, Limit = limit };
            }
        }

        return Results.Ok(new UserProfile
        {
            Id = user.Id,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Subscription = user.Subscription,
            Quotas = quotas
        });
    }

    private static async Task<IResult> GetUsageEvents(HttpContext ctx, IApiDbService db, int limit = 50, int offset = 0)
    {
        var userId = GetUserId(ctx);
        var events = await db.GetUsageEventsAsync(userId, Math.Min(limit, 100), offset);
        return Results.Ok(events);
    }

    private static async Task<IResult> GetUsageSummary(HttpContext ctx, IApiDbService db)
    {
        var userId = GetUserId(ctx);
        var yearMonth = DateTime.UtcNow.ToString("yyyy-MM");
        var usage = await db.GetAllMonthlyUsageAsync(userId, yearMonth);
        return Results.Ok(new { year_month = yearMonth, usage });
    }

    private static string GetUserId(HttpContext ctx)
        => ctx.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? ctx.User.FindFirst("oid")?.Value
        ?? throw new UnauthorizedAccessException("Missing user identifier");
}
