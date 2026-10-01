using System.Security.Cryptography;
using System.Text.Json;

namespace GptImageCli;

internal static class QueueSubmission
{
    public static long Submit(QueuePaths paths, QueueStore store, QueueSettings settings, CliOptions options, string? name)
    {
        var policy = settings.ForModel(options.LogicalImageModel ?? options.ImageModel);
        if (!policy.Enabled) throw new CliException("该队列未启用；请先修改 queue-settings.json，未入队。");
        if (name is not null && (name.Length > 120 || name.Any(char.IsControl)))
            throw new CliException("任务名称最多 120 字符，不能含控制字符。");
        var target = Path.GetFullPath(options.OutputPath);
        if (Directory.Exists(options.OutputPath) || Path.EndsInDirectorySeparator(options.OutputPath) || !Path.HasExtension(options.OutputPath))
            target = Path.Combine(target, $"gpt-image-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.{(options.OutputFormat == "jpeg" ? "jpg" : options.OutputFormat)}");
        var outputDirectory = Path.GetDirectoryName(target)!;
        var queueRelative = Path.GetRelativePath(outputDirectory, paths.Root);
        if (queueRelative == "." || (!Path.IsPathRooted(queueRelative) && queueRelative != ".." &&
            !queueRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new CliException("图片输出目录不能包含队列数据目录；请将 --queue-dir 与图片交付目录分开，避免混入数据库、配置或锁文件。未入队。");
        options = options with { OutputPath = target };
        ImageResultWriter.ValidateOutputTargets(options);
        var inputs = Path.Combine(paths.InputsDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            string Copy(string source, string prefix)
            {
                Directory.CreateDirectory(inputs);
                var destination = Path.Combine(inputs, prefix + Path.GetExtension(source));
                File.Copy(source, destination, overwrite: false);
                return destination;
            }
            options = options with
            {
                ReferenceImagePaths = options.ReferenceImagePaths.Select((p, i) => Copy(p, "reference-" + i)).ToArray(),
                MaskPath = options.MaskPath is { } mask ? Copy(mask, "mask") : null
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(options, QueueJsonContext.Default.CliOptions);
            byte[] encrypted;
            try { encrypted = Protect(json); }
            finally { CryptographicOperations.ZeroMemory(json); }
            return store.Enqueue(policy.Id, name ?? Path.GetFileNameWithoutExtension(target), encrypted,
                ImageResultWriter.ResolveOutputPaths(target, options.OutputFormat, options.Count)
                    .SelectMany(path => new[] { path, Path.ChangeExtension(path, ".txt") }).ToArray());
        }
        catch
        {
            // Only our newly created input snapshot; never delete user input or output files.
            if (Directory.Exists(inputs)) Directory.Delete(inputs, recursive: true);
            throw;
        }
    }

    private static byte[] Protect(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new CliException("持久队列目前仅支持 Windows。");
        return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    }

    public static CliOptions Decode(byte[] payload)
    {
        if (!OperatingSystem.IsWindows()) throw new CliException("持久队列目前仅支持 Windows。");
        var json = ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize(json, QueueJsonContext.Default.CliOptions)
                ?? throw new CliException("任务参数为空。");
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }
}