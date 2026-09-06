using System.Text;
using System.Text.Json;

namespace GptImageCli;

internal static class RequestFactory
{
    public static HttpRequestMessage Create(CliOptions options)
    {
        var url = BuildUrl(options);
        var body = options.Mode == ApiMode.Responses
            ? BuildResponsesBody(options)
            : BuildImagesBody(options);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ExpectContinue = false;

        if (options.Mode == ApiMode.Responses && !string.IsNullOrWhiteSpace(options.ImageModel))
            request.Headers.TryAddWithoutValidation("x-ms-oai-image-generation-deployment", options.ImageModel);

        return request;
    }

    private static Uri BuildUrl(CliOptions options)
    {
        var endpoint = options.Endpoint.TrimEnd('/');
        if (LooksLikeFullApiUrl(endpoint, options.Mode))
            return AppendApiVersionIfNeeded(endpoint, options.ApiVersion);

        var lower = endpoint.ToLowerInvariant();
        var path = options.Mode == ApiMode.Responses ? "responses" : "images/generations";
        string url;

        if (lower.EndsWith("/openai/v1") || lower.EndsWith("/v1"))
            url = $"{endpoint}/{path}";
        else if (lower.EndsWith("/openai"))
            url = $"{endpoint}/v1/{path}";
        else if (lower.Contains("openai.azure.com"))
            url = $"{endpoint}/openai/v1/{path}";
        else
            url = $"{endpoint}/v1/{path}";

        return AppendApiVersionIfNeeded(url, options.ApiVersion);
    }

    private static bool LooksLikeFullApiUrl(string endpoint, ApiMode mode)
    {
        var expected = mode == ApiMode.Responses ? "/responses" : "/images/generations";
        return endpoint.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri AppendApiVersionIfNeeded(string url, string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion) || url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
            return new Uri(url);

        var separator = url.Contains('?') ? '&' : '?';
        return new Uri($"{url}{separator}api-version={Uri.EscapeDataString(apiVersion)}");
    }

    private static Dictionary<string, object> BuildResponsesBody(CliOptions options)
    {
        var imageTool = new Dictionary<string, object>
        {
            ["type"] = "image_generation",
            ["size"] = options.Size,
            ["quality"] = options.Quality,
            ["output_format"] = options.OutputFormat
        };

        var body = new Dictionary<string, object>
        {
            ["model"] = options.TextModel,
            ["input"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new[] { new { type = "input_text", text = options.Prompt } }
                }
            },
            ["tools"] = new[] { imageTool },
            ["tool_choice"] = new { type = "image_generation" }
        };

        if (options.Count > 1)
            body["instructions"] = $"Generate exactly {options.Count} images. Each image should be a distinct variation.";

        return body;
    }

    private static Dictionary<string, object> BuildImagesBody(CliOptions options)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = options.ImageModel,
            ["prompt"] = options.Prompt,
            ["size"] = options.Size,
            ["quality"] = options.Quality,
            ["output_format"] = options.OutputFormat
        };

        if (options.Count > 1)
            body["n"] = options.Count;

        return body;
    }
}
