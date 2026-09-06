namespace GptImageCli;

internal enum ApiMode
{
    Responses,
    Images
}

internal enum AuthMode
{
    Bearer,
    ApiKey
}

internal sealed class CliOptions
{
    public required string Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required string Prompt { get; init; }
    public ApiMode Mode { get; init; } = ApiMode.Responses;
    public AuthMode AuthMode { get; init; } = AuthMode.Bearer;
    public string TextModel { get; init; } = "gpt-4.1";
    public string ImageModel { get; init; } = "gpt-image-2";
    public string? ApiVersion { get; init; }
    public string Size { get; init; } = "1024x1024";
    public string Quality { get; init; } = "medium";
    public string OutputFormat { get; init; } = "png";
    public int Count { get; init; } = 1;
    public string OutputPath { get; init; } = ".";
    public int TimeoutMinutes { get; init; } = 10;
}
