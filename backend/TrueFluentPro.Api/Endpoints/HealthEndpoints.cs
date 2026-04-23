using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// Health check endpoints: /health (liveness) and /health/ready (readiness with DB probe).
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            api_version = "1.0",
            min_client_version = "2.0.0",
            timestamp = DateTime.UtcNow
        }));

        app.MapGet("/health/ready", async (IApiDbService db) =>
        {
            try
            {
                // Probe DB connectivity
                await db.GetPlanAsync("free");
                return Results.Ok(new
                {
                    status = "ready",
                    database = "connected",
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    status = "not_ready",
                    database = "disconnected",
                    error = ex.Message,
                    timestamp = DateTime.UtcNow
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
