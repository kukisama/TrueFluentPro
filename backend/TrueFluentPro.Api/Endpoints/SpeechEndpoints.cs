using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P2: Speech proxy endpoints (batch transcribe + TTS).
/// </summary>
public static class SpeechEndpoints
{
    public static void MapSpeechEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/speech").RequireAuthorization();

        group.MapPost("/transcribe", HandleTranscribe);
        group.MapGet("/transcribe/{id}", HandleTranscribeStatus);
        group.MapPost("/synthesize", HandleSynthesize);
    }

    private static async Task HandleTranscribe(
        HttpContext ctx,
        ICredentialProvider credentials,
        IQuotaCache quotaCache,
        IApiDbService db,
        IHttpClientFactory httpFactory)
    {
        var userId = GetUserId(ctx);
        var speechKey = await credentials.GetCredentialAsync("SPEECH_KEY");
        var speechRegion = await credentials.GetCredentialAsync("SPEECH_REGION");

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "Speech credentials not configured" });
            return;
        }

        // Forward the batch transcription request to Azure Speech
        var client = httpFactory.CreateClient("AzureSpeech");
        var upstreamUrl = $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/v3.2/transcriptions";

        using var bodyStream = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyStream);

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
        request.Content = new ByteArrayContent(bodyStream.ToArray());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);

        // Record usage (estimate 1 minute per request; actual duration tracked on completion)
        _ = RecordUsageAsync(db, quotaCache, userId, "speech_minute", 1, "/api/v1/speech/transcribe",
            (int)response.StatusCode);
    }

    private static async Task HandleTranscribeStatus(
        string id,
        HttpContext ctx,
        ICredentialProvider credentials,
        IHttpClientFactory httpFactory)
    {
        var speechKey = await credentials.GetCredentialAsync("SPEECH_KEY");
        var speechRegion = await credentials.GetCredentialAsync("SPEECH_REGION");

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "Speech credentials not configured" });
            return;
        }

        var client = httpFactory.CreateClient("AzureSpeech");
        var upstreamUrl = $"https://{speechRegion}.api.cognitive.microsoft.com/speechtotext/v3.2/transcriptions/{id}";

        using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);

        using var response = await client.SendAsync(request);
        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.Content.CopyToAsync(ctx.Response.Body);
    }

    private static async Task HandleSynthesize(
        HttpContext ctx,
        ICredentialProvider credentials,
        IQuotaCache quotaCache,
        IApiDbService db,
        IHttpClientFactory httpFactory)
    {
        var userId = GetUserId(ctx);
        var speechKey = await credentials.GetCredentialAsync("SPEECH_KEY");
        var speechRegion = await credentials.GetCredentialAsync("SPEECH_REGION");

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsJsonAsync(new { error = "Speech credentials not configured" });
            return;
        }

        var client = httpFactory.CreateClient("AzureSpeech");
        var upstreamUrl = $"https://{speechRegion}.tts.speech.microsoft.com/cognitiveservices/v1";

        using var bodyStream = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyStream);

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("Ocp-Apim-Subscription-Key", speechKey);
        request.Content = new ByteArrayContent(bodyStream.ToArray());

        // Forward content type from client (SSML or plain text)
        var clientContentType = ctx.Request.ContentType ?? "application/ssml+xml";
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(clientContentType);

        // Forward output format header
        if (ctx.Request.Headers.TryGetValue("X-Microsoft-OutputFormat", out var outputFormat))
            request.Headers.Add("X-Microsoft-OutputFormat", outputFormat.ToString());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType != null)
            ctx.Response.ContentType = response.Content.Headers.ContentType.ToString();
        await response.Content.CopyToAsync(ctx.Response.Body);

        _ = RecordUsageAsync(db, quotaCache, userId, "speech_minute", 1, "/api/v1/speech/synthesize",
            (int)response.StatusCode);
    }

    private static string GetUserId(HttpContext ctx)
        => ctx.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? ctx.User.FindFirst("oid")?.Value
        ?? "anonymous";

    private static async Task RecordUsageAsync(IApiDbService db, IQuotaCache quotaCache,
        string userId, string resourceType, long amount, string endpoint, int statusCode)
    {
        try
        {
            await quotaCache.IncrementUsageAsync(userId, resourceType, amount);
            await db.RecordUsageEventAsync(new UsageEvent
            {
                UserId = userId,
                ResourceType = resourceType,
                Amount = amount,
                EndpointPath = endpoint,
                StatusCode = statusCode
            });
        }
        catch { /* fire-and-forget */ }
    }
}
