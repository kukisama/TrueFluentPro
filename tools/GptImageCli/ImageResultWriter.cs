namespace GptImageCli;

internal static class ImageResultWriter
{
    public static async Task<IReadOnlyList<string>> SaveAsync(
        IReadOnlyList<ImagePayload> images,
        string outputPath,
        string outputFormat,
        HttpClient httpClient)
    {
        var paths = ResolveOutputPaths(outputPath, outputFormat, images.Count);
        var saved = new List<string>(images.Count);

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            byte[] bytes;
            if (!string.IsNullOrWhiteSpace(image.Base64))
            {
                bytes = Convert.FromBase64String(image.Base64);
            }
            else if (!string.IsNullOrWhiteSpace(image.Url))
            {
                bytes = await httpClient.GetByteArrayAsync(image.Url);
            }
            else
            {
                continue;
            }

            var path = paths[i];
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            await File.WriteAllBytesAsync(path, bytes);
            saved.Add(path);
        }

        return saved;
    }

    private static List<string> ResolveOutputPaths(string outputPath, string outputFormat, int count)
    {
        var normalizedOutput = string.IsNullOrWhiteSpace(outputPath) ? "." : outputPath;
        var extension = outputFormat == "jpeg" ? ".jpg" : $".{outputFormat}";
        var hasExtension = Path.HasExtension(normalizedOutput);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var paths = new List<string>(count);

        if (!hasExtension)
        {
            for (var i = 0; i < count; i++)
                paths.Add(Path.GetFullPath(Path.Combine(normalizedOutput, $"gpt-image-{timestamp}-{i + 1:00}{extension}")));
            return paths;
        }

        var directory = Path.GetDirectoryName(normalizedOutput) ?? ".";
        var name = Path.GetFileNameWithoutExtension(normalizedOutput);
        var fileExtension = Path.GetExtension(normalizedOutput);

        for (var i = 0; i < count; i++)
        {
            var fileName = count == 1 ? $"{name}{fileExtension}" : $"{name}-{i + 1:00}{fileExtension}";
            paths.Add(Path.GetFullPath(Path.Combine(directory, fileName)));
        }

        return paths;
    }
}
