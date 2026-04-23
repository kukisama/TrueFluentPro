using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TrueFluentPro.Api.Endpoints;
using TrueFluentPro.Api.Middleware;
using TrueFluentPro.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════════
// ① Database: auto-detect PostgreSQL vs SQLite
// ═══════════════════════════════════════════════════════════════
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
if (!string.IsNullOrEmpty(databaseUrl))
{
    // PostgreSQL — for now, fall through to SQLite until Npgsql service is implemented
    // TODO: Add NpgsqlApiDbService implementation for PostgreSQL
    var dbPath = Path.Combine(
        Environment.GetEnvironmentVariable("DATA_DIR") ?? "/app/data",
        "truefluentpro.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    builder.Services.AddSingleton<IApiDbService>(sp =>
        new SqliteApiDbService(dbPath, sp.GetRequiredService<ILogger<SqliteApiDbService>>()));
}
else
{
    // SQLite (default — 极简 mode)
    var dbPath = Path.Combine(
        Environment.GetEnvironmentVariable("DATA_DIR") ?? "/app/data",
        "truefluentpro.db");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    builder.Services.AddSingleton<IApiDbService>(sp =>
        new SqliteApiDbService(dbPath, sp.GetRequiredService<ILogger<SqliteApiDbService>>()));
}

// ═══════════════════════════════════════════════════════════════
// ② Authentication: AAD JWT Bearer
// ═══════════════════════════════════════════════════════════════
var aadTenantId = Environment.GetEnvironmentVariable("AAD_TENANT_ID") ?? "common";
var aadClientId = Environment.GetEnvironmentVariable("AAD_CLIENT_ID") ?? "";
var aadAudience = Environment.GetEnvironmentVariable("AAD_AUDIENCE") ?? $"api://{aadClientId}";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{aadTenantId}/v2.0";
        options.Audience = aadAudience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrEmpty(aadTenantId) && aadTenantId != "common",
            ValidIssuer = $"https://login.microsoftonline.com/{aadTenantId}/v2.0",
            ValidateAudience = !string.IsNullOrEmpty(aadAudience),
            ValidAudience = aadAudience,
            ValidateLifetime = true,
            NameClaimType = "name",
            RoleClaimType = "roles"
        };

        // Support WebSocket authentication via query string token
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/wss"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            // Check for admin role or admin claim
            return context.User.HasClaim(c => c.Type == "roles" && c.Value == "admin")
                || context.User.HasClaim(c => c.Type == ClaimTypes.Role && c.Value == "admin");
        });
    });
});

// ═══════════════════════════════════════════════════════════════
// ③ Rate Limiting: per-user sliding window
// ═══════════════════════════════════════════════════════════════
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("PerUser", httpContext =>
    {
        var userId = httpContext.User.FindFirst("oid")?.Value
                  ?? httpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";

        return RateLimitPartition.GetSlidingWindowLimiter(userId, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 5
        });
    });

    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = "Too many requests. Please slow down.",
            retry_after_seconds = 10
        });
    };
});

// ═══════════════════════════════════════════════════════════════
// ④ Services
// ═══════════════════════════════════════════════════════════════
builder.Services.AddSingleton<ICredentialProvider, CredentialProvider>();
builder.Services.AddSingleton<IQuotaCache, InMemoryQuotaCache>();
builder.Services.AddHttpClient("AzureOpenAI", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5); // Long timeout for streaming
});
builder.Services.AddHttpClient("AzureSpeech", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10); // Batch transcription can be slow
});

// ═══════════════════════════════════════════════════════════════
// ⑤ Kestrel configuration
// ═══════════════════════════════════════════════════════════════
builder.WebHost.ConfigureKestrel(options =>
{
    // HTTP port (always open)
    options.ListenAnyIP(8080);

    // HTTPS port (optional, if cert configured)
    var certPath = Environment.GetEnvironmentVariable("TLS_CERT_PATH");
    var keyPath = Environment.GetEnvironmentVariable("TLS_KEY_PATH");
    if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
    {
        var cert = System.Security.Cryptography.X509Certificates.X509Certificate2
            .CreateFromPemFile(certPath, keyPath);
        options.ListenAnyIP(8443, listenOptions =>
        {
            listenOptions.UseHttps(cert);
        });
    }
});

// Request body size limit (100 MB for audio uploads)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024;
});

var app = builder.Build();

// ═══════════════════════════════════════════════════════════════
// Initialize database on startup
// ═══════════════════════════════════════════════════════════════
var dbService = app.Services.GetRequiredService<IApiDbService>();
await dbService.InitializeAsync();

// ═══════════════════════════════════════════════════════════════
// Middleware pipeline (order matters!)
// ═══════════════════════════════════════════════════════════════

// ① Global exception handler
app.UseExceptionHandler(appBuilder =>
{
    appBuilder.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = "internal_server_error",
            message = "An unexpected error occurred"
        });
    });
});

// ② Authentication
app.UseAuthentication();
app.UseAuthorization();

// ③ Rate limiting
app.UseRateLimiter();

// ④ WebSocket support
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// ⑤ Quota check middleware
app.UseMiddleware<QuotaMiddleware>();

// ═══════════════════════════════════════════════════════════════
// Map endpoints
// ═══════════════════════════════════════════════════════════════

// Health checks (no auth)
app.MapHealthEndpoints();

// P0: Chat proxy
app.MapChatEndpoints();

// P1: User profile + usage
app.MapUserEndpoints();

// P2: Full endpoint proxy
app.MapSpeechEndpoints();
app.MapImageEndpoints();
app.MapVideoEndpoints();
app.MapStorageEndpoints();

// P3: WebSocket realtime translation
app.MapTranslateWebSocket();

// P6: Admin management
app.MapAdminEndpoints();

app.Logger.LogInformation("TrueFluentPro API started on port 8080");
app.Logger.LogInformation("Database: {DbType}", string.IsNullOrEmpty(databaseUrl) ? "SQLite" : "PostgreSQL");

await app.RunAsync();
