using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P6: Admin management endpoints (requires admin role).
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin")
            .RequireAuthorization("AdminPolicy");

        // Users
        group.MapGet("/users", ListUsers);
        group.MapPut("/users/{id}", UpdateUser);

        // Credentials
        group.MapGet("/credentials", ListCredentials);
        group.MapPut("/credentials/{id}", UpsertCredential);

        // Stats
        group.MapGet("/stats", GetStats);

        // Plans
        group.MapGet("/plans", ListPlans);
        group.MapPut("/plans/{id}", UpsertPlan);
    }

    private static async Task<IResult> ListUsers(IApiDbService db, int limit = 100, int offset = 0)
    {
        var users = await db.ListUsersAsync(Math.Min(limit, 100), offset);
        return Results.Ok(users);
    }

    private static async Task<IResult> UpdateUser(string id, HttpContext ctx, IApiDbService db)
    {
        var body = await ctx.Request.ReadFromJsonAsync<UpdateUserRequest>();
        if (body == null) return Results.BadRequest("Invalid request body");

        await db.UpdateUserAsync(id, body.Subscription, body.IsActive, body.IsAdmin);
        var updated = await db.GetUserAsync(id);
        return updated != null ? Results.Ok(updated) : Results.NotFound();
    }

    private static async Task<IResult> ListCredentials(IApiDbService db)
    {
        var creds = await db.ListCredentialsAsync();
        return Results.Ok(creds);
    }

    private static async Task<IResult> UpsertCredential(string id, HttpContext ctx, IApiDbService db)
    {
        var body = await ctx.Request.ReadFromJsonAsync<UpsertCredentialRequest>();
        if (body == null || string.IsNullOrEmpty(body.Value))
            return Results.BadRequest("Missing credential value");

        await db.UpsertCredentialAsync(id, body.ServiceType ?? "unknown", body.Value, body.MetadataJson);
        return Results.Ok(new { id, status = "updated" });
    }

    private static async Task<IResult> GetStats(IApiDbService db)
    {
        var stats = await db.GetSystemStatsAsync();
        return Results.Ok(stats);
    }

    private static async Task<IResult> ListPlans(IApiDbService db)
    {
        var plans = await db.ListPlansAsync();
        return Results.Ok(plans);
    }

    private static async Task<IResult> UpsertPlan(string id, HttpContext ctx, IApiDbService db)
    {
        var body = await ctx.Request.ReadFromJsonAsync<SubscriptionPlan>();
        if (body == null) return Results.BadRequest("Invalid request body");

        body.PlanId = id;
        await db.UpsertPlanAsync(body);
        return Results.Ok(body);
    }

    private sealed class UpdateUserRequest
    {
        public string? Subscription { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsAdmin { get; set; }
    }

    private sealed class UpsertCredentialRequest
    {
        public string? ServiceType { get; set; }
        public string Value { get; set; } = string.Empty;
        public string? MetadataJson { get; set; }
    }
}
