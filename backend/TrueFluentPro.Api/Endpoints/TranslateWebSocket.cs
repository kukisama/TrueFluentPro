using System.Net.WebSockets;
using TrueFluentPro.Api.Models;
using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P3: WebSocket realtime translation bridge.
/// Client ←WSS→ ASP.NET Core ←WSS→ Azure Speech Translation.
/// </summary>
public static class TranslateWebSocket
{
    public static void MapTranslateWebSocket(this WebApplication app)
    {
        app.Map("/wss/v1/translate", HandleTranslateWebSocket)
           .RequireAuthorization();
    }

    private static async Task HandleTranslateWebSocket(
        HttpContext context,
        ICredentialProvider credentials,
        IQuotaCache quotaCache,
        IApiDbService db,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("TranslateWebSocket");

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "WebSocket connection required" });
            return;
        }

        var userId = context.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? context.User.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var speechKey = await credentials.GetCredentialAsync("SPEECH_KEY");
        var speechRegion = await credentials.GetCredentialAsync("SPEECH_REGION");

        if (string.IsNullOrEmpty(speechKey) || string.IsNullOrEmpty(speechRegion))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Speech credentials not configured" });
            return;
        }

        // Parse translation parameters from query string
        var fromLang = context.Request.Query["from"].FirstOrDefault() ?? "zh-CN";
        var toLang = context.Request.Query["to"].FirstOrDefault() ?? "en";

        // Sanitize user-provided language params for safe logging
        var safeFromLang = SanitizeForLog(fromLang);
        var safeToLang = SanitizeForLog(toLang);

        // Accept client WebSocket
        using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
        var startTime = DateTime.UtcNow;

        try
        {
            // Build Azure Speech Translation WebSocket URL
            var azureWsUrl = $"wss://{speechRegion}.stt.speech.microsoft.com/speech/universal/v2?" +
                             $"Ocp-Apim-Subscription-Key={speechKey}&" +
                             $"X-ConnectionId={Guid.NewGuid()}&" +
                             $"language={fromLang}";

            using var azureWs = new ClientWebSocket();
            azureWs.Options.SetRequestHeader("Ocp-Apim-Subscription-Key", speechKey);

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await azureWs.ConnectAsync(new Uri(azureWsUrl), connectCts.Token);

            logger.LogInformation("WebSocket bridge established for user {UserId}: {From} → {To}", userId, safeFromLang, safeToLang);

            // Bidirectional pipe
            using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var clientToAzure = PipeAsync(clientWs, azureWs, bridgeCts.Token, "client→azure", logger);
            var azureToClient = PipeAsync(azureWs, clientWs, bridgeCts.Token, "azure→client", logger);

            // Wait for either direction to complete
            var completed = await Task.WhenAny(clientToAzure, azureToClient);
            await bridgeCts.CancelAsync();

            // Gracefully close both sides
            await CloseWebSocketSafely(clientWs, logger);
            await CloseWebSocketSafely(azureWs, logger);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket error for user {UserId}", userId);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("WebSocket bridge cancelled for user {UserId}", userId);
        }
        finally
        {
            // Record usage: connection duration in minutes
            var duration = DateTime.UtcNow - startTime;
            var minutes = (long)Math.Ceiling(duration.TotalMinutes);
            if (minutes > 0)
            {
                try
                {
                    await quotaCache.IncrementUsageAsync(userId, "translate_minute", minutes);
                    await db.RecordUsageEventAsync(new UsageEvent
                    {
                        UserId = userId,
                        ResourceType = "translate_minute",
                        Amount = minutes,
                        EndpointPath = "/wss/v1/translate",
                        DurationMs = (int)duration.TotalMilliseconds
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to record translate usage for user {UserId}", userId);
                }
            }

            logger.LogInformation("WebSocket bridge closed for user {UserId}, duration: {Duration:F1}min", userId, duration.TotalMinutes);
        }
    }

    private static async Task PipeAsync(WebSocket source, WebSocket target, CancellationToken ct, string direction, ILogger logger)
    {
        var buffer = new byte[8192];
        try
        {
            while (source.State == WebSocketState.Open && target.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogDebug("WebSocket {Direction}: close received", direction);
                    break;
                }

                if (target.State == WebSocketState.Open)
                {
                    await target.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        ct);
                }
            }
        }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "WebSocket pipe {Direction} ended", direction);
        }
        catch (OperationCanceledException) { }
    }

    private static async Task CloseWebSocketSafely(WebSocket ws, ILogger logger)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bridge closed", cts.Token);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "WebSocket close failed (expected if already closed)");
        }
    }

    /// <summary>
    /// Sanitize user-provided values for safe logging to prevent log forging.
    /// </summary>
    private static string SanitizeForLog(string value)
        => new(value.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').Take(20).ToArray());
}
