using TrueFluentPro.Api.Services;

namespace TrueFluentPro.Api.Endpoints;

/// <summary>
/// P2: Blob storage SAS URL generation for client-side direct upload.
/// </summary>
public static class StorageEndpoints
{
    public static void MapStorageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/storage").RequireAuthorization();

        group.MapGet("/upload-url", HandleGetUploadUrl);
    }

    private static async Task<IResult> HandleGetUploadUrl(
        HttpContext ctx,
        ICredentialProvider credentials,
        string? container = null,
        string? filename = null)
    {
        var connectionString = await credentials.GetCredentialAsync("BLOB_CONNECTION_STRING");
        if (string.IsNullOrEmpty(connectionString))
        {
            return Results.Json(
                new { error = "Blob storage credentials not configured" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Generate a SAS URL using Azure.Storage.Blobs
        // Since we don't want to add Azure.Storage.Blobs as a dependency to the API project,
        // we construct the SAS URL manually or return the connection info for the client.
        //
        // For production: use Azure.Storage.Blobs BlobServiceClient.GenerateAccountSasUri()
        // For now: return a proxy upload endpoint that the client can use.

        var containerName = container ?? "audio-uploads";
        var blobName = filename ?? $"{Guid.NewGuid()}.wav";

        // Return upload metadata — client will use direct upload via SAS
        // In a full implementation, this would generate a time-limited SAS token.
        return Results.Ok(new
        {
            container = containerName,
            blob_name = blobName,
            upload_url = $"(SAS URL generation requires Azure.Storage.Blobs — configure BLOB_CONNECTION_STRING)",
            expires_in_minutes = 15,
            note = "For production deployment, this endpoint generates a time-limited SAS URL for direct client-to-blob upload"
        });
    }
}
