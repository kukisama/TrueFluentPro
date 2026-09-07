using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GptImageCli;

internal static class RequestFactory
{
    public static HttpRequestMessage Create(CliOptions options)
    {
        var url = BuildUrl(options);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = options.Mode == ApiMode.Edit
                ? BuildEditContent(options)
                : new StringContent(JsonSerializer.Serialize(options.Mode == ApiMode.Responses
                    ? BuildResponsesBody(options)
                    : BuildImagesBody(options), JsonSerializationContext.Default.RequestBody), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.ExpectContinue = false;

        if (options.Mode == ApiMode.Responses && !string.IsNullOrWhiteSpace(options.ImageModel))
            request.Headers.TryAddWithoutValidation("x-ms-oai-image-generation-deployment", options.ImageModel);

        return request;
    }

    private static Uri BuildUrl(CliOptions options)
    {
        if (options.ConfiguredRequestUrl is { } configured)
        {
            if (options.ApiVersion is { } version)
                configured = configured.Split('?')[0] + "?api-version=" + Uri.EscapeDataString(version);
            return new Uri(configured);
        }
        var endpoint = options.Endpoint.TrimEnd('/');
        if (LooksLikeFullApiUrl(endpoint, options.Mode))
            return AppendApiVersionIfNeeded(endpoint, options.ApiVersion);

        var lower = endpoint.ToLowerInvariant();
        var path = GetApiPath(options.Mode);
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

    internal static string JoinConfiguredPath(string baseUrl, string suffix)
    {
        // 仅版本化路由复用已有版本尾段；不修改配置 BaseUrl 的密钥匹配身份。
        // APIM /images/* 和 /responses 是 raw 路由，完整保留其基地址路径。
        var versionPrefix = suffix.StartsWith("/openai/v1/", StringComparison.Ordinal) ? "/openai/v1/"
            : suffix.StartsWith("/v1/", StringComparison.Ordinal) ? "/v1/" : null;
        if (versionPrefix is not null)
        {
            var path = new Uri(baseUrl).AbsolutePath;
            if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return baseUrl + "/" + suffix[versionPrefix.Length..];
            if (path.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
                return baseUrl + "/v1/" + suffix[versionPrefix.Length..];
        }
        return baseUrl + suffix;
    }

    private static bool LooksLikeFullApiUrl(string endpoint, ApiMode mode)
    {
        var expected = "/" + GetApiPath(mode);
        return new Uri(endpoint).AbsolutePath.TrimEnd('/').EndsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetApiPath(ApiMode mode) => mode switch
    {
        ApiMode.Responses => "responses",
        ApiMode.Edit => "images/edits",
        _ => "images/generations"
    };

    private static MultipartFormDataContent BuildEditContent(CliOptions options)
    {
        var content = new MultipartFormDataContent();
        try
        {
            content.Add(new StringContent(options.ImageModel), "model");
            content.Add(new StringContent(options.Prompt), "prompt");
            content.Add(new StringContent(options.Size), "size");
            content.Add(new StringContent(options.Quality), "quality");
            content.Add(new StringContent(options.OutputFormat), "output_format");
            content.Add(new StringContent(options.Count.ToString(CultureInfo.InvariantCulture)), "n");
            var optional = new Dictionary<string, object>();
            AddOptionalFields(optional, options);
            foreach (var field in optional)
                content.Add(new StringContent(Convert.ToString(field.Value, CultureInfo.InvariantCulture)!), field.Key);
            if (options.MaskPath is { } mask)
            {
                var part = new ByteArrayContent(File.ReadAllBytes(mask));
                part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                content.Add(part, "mask", Path.GetFileName(mask));
            }

            foreach (var path in options.ReferenceImagePaths)
            {
                var image = new StreamContent(File.OpenRead(path));
                try
                {
                    image.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".webp" => "image/webp",
                        _ => "image/png"
                    });
                    content.Add(image, options.ReferenceImagePaths.Count == 1 ? "image" : "image[]", Path.GetFileName(path));
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }

            return content;
        }
        catch
        {
            content.Dispose();
            throw;
        }
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
                new ResponsesInput("user", [new ResponsesContent("input_text", options.Prompt)])
            },
            ["tools"] = new[] { imageTool },
            ["tool_choice"] = new ImageToolChoice("image_generation")
        };

        if (options.Count > 1)
            body["instructions"] = $"Generate exactly {options.Count} images. Each image should be a distinct variation.";

        AddOptionalFields(imageTool, options);
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

        AddOptionalFields(body, options);
        return body;
    }

    private static void AddOptionalFields(Dictionary<string, object> fields, CliOptions options)
    {
        if (options.Background is { } background) fields["background"] = background;
        if (options.OutputCompression is { } compression) fields["output_compression"] = compression;
        if (options.Moderation is { } moderation) fields["moderation"] = moderation;
        if (options.User is { } user) fields["user"] = user;
    }
}
