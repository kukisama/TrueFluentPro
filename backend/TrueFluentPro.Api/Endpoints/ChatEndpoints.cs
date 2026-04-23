using System.Net.Http.Headers;
using System.Text.Json;
using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P0: Chat completions proxy → Azure OpenAI (streaming + sync).
/// </summary>
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/chat").RequireAuthorization();

        // Non-streaming chat completions
        group.MapPost("/completions/sync", HandleChatSync);

        // Streaming chat completions (SSE)
        group.MapPost("/completions", HandleChatStream);
    }

    private static async Task HandleChatSync(
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

        // Read and forward request body
        using var bodyStream = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(bodyStream);
        var bodyBytes = bodyStream.ToArray();

        // Parse to get model/deployment name
        var requestJson = JsonDocument.Parse(bodyBytes);
        var model = requestJson.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "gpt-4o" : "gpt-4o";

        // Build upstream request
        var client = httpFactory.CreateClient("AzureOpenAI");
        var upstreamUrl = $"{endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-12-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("api-key", apiKey);
        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request);
        sw.Stop();

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        await ctx.Response.Body.WriteAsync(responseBytes);

        // Record usage async
        _ = Task.Run(async () =>
        {
            try
            {
                var tokenCount = ExtractTokenCount(responseBytes);
                if (tokenCount > 0)
                {
                    await quotaCache.IncrementUsageAsync(userId, "chat_token", tokenCount);
                    await db.RecordUsageEventAsync(new UsageEvent
                    {
                        UserId = userId,
                        ResourceType = "chat_token",
                        Amount = tokenCount,
                        Model = model,
                        EndpointPath = "/api/v1/chat/completions/sync",
                        StatusCode = (int)response.StatusCode,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }
            }
            catch { /* fire-and-forget */ }
        });
    }

    private static async Task HandleChatStream(
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
        var bodyBytes = bodyStream.ToArray();

        var requestJson = JsonDocument.Parse(bodyBytes);
        var model = requestJson.RootElement.TryGetProperty("model", out var m) ? m.GetString() ?? "gpt-4o" : "gpt-4o";

        // Force stream: true in the request
        var modifiedBody = EnsureStreamTrue(bodyBytes);

        var client = httpFactory.CreateClient("AzureOpenAI");
        var upstreamUrl = $"{endpoint.TrimEnd('/')}/openai/deployments/{model}/chat/completions?api-version=2024-12-01-preview";

        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Add("api-key", apiKey);
        request.Content = new ByteArrayContent(modifiedBody);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        sw.Stop();

        ctx.Response.StatusCode = (int)response.StatusCode;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        // Stream SSE directly from Azure to client
        long totalTokens = 0;
        await using var upstreamStream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(upstreamStream);

        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ctx.RequestAborted);
            if (line == null) break;

            await ctx.Response.WriteAsync(line + "\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            // Try to extract usage from final SSE chunk
            if (line.StartsWith("data: ") && line.Contains("\"usage\""))
            {
                totalTokens = ExtractTokenCountFromSseLine(line);
            }
        }

        // Record usage
        if (totalTokens > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await quotaCache.IncrementUsageAsync(userId, "chat_token", totalTokens);
                    await db.RecordUsageEventAsync(new UsageEvent
                    {
                        UserId = userId,
                        ResourceType = "chat_token",
                        Amount = totalTokens,
                        Model = model,
                        EndpointPath = "/api/v1/chat/completions",
                        StatusCode = (int)response.StatusCode,
                        DurationMs = (int)sw.ElapsedMilliseconds
                    });
                }
                catch { /* fire-and-forget */ }
            });
        }
    }

    private static string GetUserId(HttpContext ctx)
        => ctx.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? ctx.User.FindFirst("oid")?.Value
        ?? "anonymous";

    private static long ExtractTokenCount(byte[] responseBytes)
    {
        try
        {
            var doc = JsonDocument.Parse(responseBytes);
            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var tokens))
            {
                return tokens.GetInt64();
            }
        }
        catch { }
        return 0;
    }

    private static long ExtractTokenCountFromSseLine(string line)
    {
        try
        {
            var json = line["data: ".Length..];
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var tokens))
            {
                return tokens.GetInt64();
            }
        }
        catch { }
        return 0;
    }

    private static byte[] EnsureStreamTrue(byte[] bodyBytes)
    {
        try
        {
            var doc = JsonDocument.Parse(bodyBytes);
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "stream")
                {
                    writer.WriteBoolean("stream", true);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            if (!doc.RootElement.TryGetProperty("stream", out _))
                writer.WriteBoolean("stream", true);

            writer.WriteEndObject();
            writer.Flush();
            return ms.ToArray();
        }
        catch
        {
            return bodyBytes;
        }
    }
}
