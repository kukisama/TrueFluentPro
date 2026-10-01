using System.Text;

namespace GptImageCli;

internal static class FailureResultWriter
{
    // Logs are not images: never add them to report.Files or change the original result.
    public static async Task<bool> TrySaveAsync(CliOptions options, CliReport report, string state, long? jobId = null)
    {
        try
        {
            var text = report.SerializeFailure(jobId, state);
            var written = true;
            foreach (var imagePath in ImageResultWriter.ResolveOutputPaths(options.OutputPath, options.OutputFormat, options.Count))
            {
                if (report.Files.Contains(imagePath, StringComparer.OrdinalIgnoreCase)) continue;
                var path = Path.ChangeExtension(imagePath, ".txt");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var stream = new FileStream(path, options.Overwrite ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { written = false; }
            }
            return written;
        }
        catch (Exception ex) when (ex is CliException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return false; }
    }
}