namespace GptImageCli;

internal static class ImageResultWriter
{
    public static void ValidateOutputTargets(CliOptions options)
    {
        // Responses has no known result count; protect its actual paths when saving.
        if (options.Overwrite || options.Mode == ApiMode.Responses || IsOutputDirectory(options.OutputPath)) return;
        foreach (var path in ResolveOutputPaths(options.OutputPath, options.OutputFormat, options.Count))
            if (File.Exists(path) || Directory.Exists(path))
                throw new CliException($"输出目标已存在：{path}；默认不覆盖，确认后可使用 --overwrite。");
    }

    public static async Task<IReadOnlyList<string>> SaveAsync(
        IReadOnlyList<ImagePayload> images,
        string outputPath,
        string outputFormat,
        HttpClient httpClient,
        ICollection<string>? savedFiles = null,
        bool overwrite = false)
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
            await using (var stream = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await stream.WriteAsync(bytes);
            saved.Add(path);
            savedFiles?.Add(path);
        }

        return saved;
    }

    private static bool IsOutputDirectory(string path) =>
        Directory.Exists(path) || Path.EndsInDirectorySeparator(path) || !Path.HasExtension(path);

    private static List<string> ResolveOutputPaths(string outputPath, string outputFormat, int count)
    {
        var normalizedOutput = string.IsNullOrWhiteSpace(outputPath) ? "." : outputPath;
        var extension = outputFormat == "jpeg" ? ".jpg" : $".{outputFormat}";
        var timestamp = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var paths = new List<string>(count);

        if (IsOutputDirectory(normalizedOutput))
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
