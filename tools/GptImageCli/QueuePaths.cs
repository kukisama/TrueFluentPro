using System.Text.Json;

namespace GptImageCli;

internal sealed class QueuePaths
{
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "queue-settings.json");
    public string DatabaseFile => Path.Combine(Root, "queue.db");
    public string InputsDirectory => Path.Combine(Root, "inputs");

    public QueuePaths(string? root = null)
    {
        if (!OperatingSystem.IsWindows()) throw new CliException("持久队列目前支持 Windows 10/11；同步生图不受此限制。");
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GptImageCli"));
        if (Root.StartsWith(@"\\", StringComparison.Ordinal)) throw new CliException("SQLite 队列目录必须位于本机磁盘，不能使用网络共享。");
        Directory.CreateDirectory(Root);
    }

    public FileStream? TryLock(string name)
    {
        try { return new FileStream(Path.Combine(Root, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }

    public async Task<FileStream> LockAsync(string name, CancellationToken cancellation = default)
    {
        var until = Environment.TickCount64 + 15000;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (TryLock(name) is { } file) return file;
            if (Environment.TickCount64 >= until) throw new CliException("队列正在处理其他操作，请稍后重试。");
            await Task.Delay(50, cancellation);
        }
    }

    public bool WorkerRunning()
    {
        using var lease = TryLock("worker");
        return lease is null;
    }

    public void EnsureSettings()
    {
        if (File.Exists(SettingsFile)) return;
        using var resource = typeof(QueuePaths).Assembly.GetManifestResourceStream("queue-settings.default.json")
            ?? throw new CliException("缺少内置默认队列配置。");
        var temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { resource.CopyTo(file); file.Flush(true); }
            try { File.Move(temporary, SettingsFile); }
            catch (IOException) when (File.Exists(SettingsFile)) { /* Another submitter initialized it. */ }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public QueueSettings LoadSettings()
    {
        EnsureSettings();
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), QueueJsonContext.Default.QueueSettings)
                ?? throw new CliException("队列配置不能为空。");
            settings.Validate();
            return settings;
        }
        catch (JsonException) { throw new CliException("queue-settings.json 格式无效或包含未知字段；请修正配置。"); }
    }

    public void SaveSettings(QueueSettings settings)
    {
        settings.Validate();
        var temporary = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, settings, QueueJsonContext.Default.QueueSettings); file.Flush(true); }
            File.Move(temporary, SettingsFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}