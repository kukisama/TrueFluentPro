using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P2: Image generation proxy → Azure OpenAI DALL-E.
/// </summary>
public static class ImageEndpoints
{
    public static void MapImageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/images").RequireAuthorization();

        group.MapPost("/generations", HandleImageGeneration);
    }

    private static async Task HandleImageGeneration(
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
        var upstreamUrl = $"{endpoint.TrimEnd('/')}/openai/deployments/dall-e-3/images/generations?api-version=2024-12-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("api-key", apiKey);
        request.Content = new ByteArrayContent(bodyStream.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request);
        sw.Stop();

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);

        // Record 1 image per successful generation
        if (response.IsSuccessStatusCode)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await quotaCache.IncrementUsageAsync(userId, "image", 1);
                    await db.RecordUsageEventAsync(new UsageEvent
                    {
                        UserId = userId,
                        ResourceType = "image",
                        Amount = 1,
                        Model = "dall-e-3",
                        EndpointPath = "/api/v1/images/generations",
                        StatusCode = (int)response.StatusCode,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }
                catch { }
            });
        }
    }

    private static string GetUserId(HttpContext ctx)
        => ctx.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? ctx.User.FindFirst("oid")?.Value
        ?? "anonymous";
}
