using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P2: Video generation proxy → Azure OpenAI Sora.
/// </summary>
public static class VideoEndpoints
{
    public static void MapVideoEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/videos").RequireAuthorization();

        group.MapPost("/generations", HandleVideoGeneration);
        group.MapGet("/{operationId}", HandleVideoStatus);
    }

    private static async Task HandleVideoGeneration(
        HttpContext ctx,
        ICredentialProvider credentials,
        IQuotaCache quotaCache,
        IApiDbService db,
        IHttpClientFactory httpFactory)
    {
        var userId = GetUserId(ctx);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var endpoint = await credentials.GetCredentialAsync("AOAI_ENDPOINT");
        var apiKey = await credentials.GetCredentialAsync("AOAI_KEY");

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "Azure OpenAI credentials not configured" });
            return;
        }

        using var bodyStream = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyStream);

        var client = httpFactory.CreateClient("AzureOpenAI");
        // Sora video generation endpoint
        var upstreamUrl = $"{endpoint.TrimEnd('/')}/openai/v1/video/generations?api-version=2025-04-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("api-key", apiKey);
        request.Content = new ByteArrayContent(bodyStream.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request);
        sw.Stop();

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);

        // Record estimated video seconds (actual tracked on completion)
        if (response.IsSuccessStatusCode)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await quotaCache.IncrementUsageAsync(userId, "video_second", 10); // Default estimate
                    await db.RecordUsageEventAsync(new UsageEvent
                    {
                        UserId = userId,
                        ResourceType = "video_second",
                        Amount = 10,
                        Model = "sora",
                        EndpointPath = "/api/v1/videos/generations",
                        StatusCode = (int)response.StatusCode,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }
                catch { }
            });
        }
    }

    private static async Task HandleVideoStatus(
        string operationId,
        HttpContext ctx,
        ICredentialProvider credentials,
        IHttpClientFactory httpFactory)
    {
        var endpoint = await credentials.GetCredentialAsync("AOAI_ENDPOINT");
        var apiKey = await credentials.GetCredentialAsync("AOAI_KEY");

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "Azure OpenAI credentials not configured" });
            return;
        }

        var client = httpFactory.CreateClient("AzureOpenAI");
        var upstreamUrl = $"{endpoint.TrimEnd('/')}/openai/v1/video/generations/{operationId}?api-version=2025-04-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
        request.Headers.Add("api-key", apiKey);

        using var response = await client.SendAsync(request);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);
    }

    private static string GetUserId(HttpContext ctx)
        => ctx.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? ctx.User.FindFirst("oid")?.Value
        ?? "anonymous";
}
