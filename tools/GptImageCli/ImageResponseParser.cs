using System.Text.Json;

namespace GptImageCli;

internal sealed record ImagePayload(string? Base64, string? Url);

internal static class ImageResponseParser
{
    public static IEnumerable<ImagePayload> Parse(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (TryGetString(item, "b64_json", out var base64))
                    yield return new ImagePayload(base64, null);
                else if (TryGetString(item, "url", out var url))
                    yield return new ImagePayload(null, url);
            }
        }

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!TryIsImageGenerationCall(item))
                    continue;

                if (TryGetString(item, "result", out var result))
                    yield return new ImagePayload(result, null);
                else if (item.TryGetProperty("result", out var resultElement) && TryReadImageFromObject(resultElement, out var payload))
                    yield return payload;
            }
        }
    }

    private static bool TryIsImageGenerationCall(JsonElement item)
        => TryGetString(item, "type", out var type) && type == "image_generation_call";

    private static bool TryReadImageFromObject(JsonElement element, out ImagePayload payload)
    {
        payload = default!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetString(element, "b64_json", out var base64) || TryGetString(element, "image_base64", out base64))
        {
            payload = new ImagePayload(base64, null);
            return true;
        }

        if (TryGetString(element, "url", out var url))
        {
            payload = new ImagePayload(null, url);
            return true;
        }

        return false;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }
}
