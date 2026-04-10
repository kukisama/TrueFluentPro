// ─────────────────────────────────────────────────────────────────
// Updater — 通用外部更新器
//
// 用法：Updater.exe --zip <zipPath> --target <appDir> --exe <exeName> --pid <pid>
//
// 流程：
//   1. 等待指定 PID 的主进程退出（最长 30 秒）
//   2. 备份当前文件到 _backup 目录
//   3. 解压 zip 覆盖目标目录
//   4. 清理备份和临时 zip
//   5. 重新启动主程序
//
// 设计目标：
//   - 通用性：通过命令行参数驱动，不绑定任何特定项目
//   - 安全性：失败时自动回滚到备份
//   - 独立性：无第三方依赖，FDD 单文件发布
// ─────────────────────────────────────────────────────────────────

using System.Diagnostics;
using System.IO.Compression;

var options = ParseArgs(Environment.GetCommandLineArgs().Skip(1).ToArray());
if (!options.TryGetValue("zip", out var zipPath) ||
    !options.TryGetValue("target", out var targetDir) ||
    !options.TryGetValue("exe", out var exeName))
{
    Console.Error.WriteLine("用法: Updater.exe --zip <zipPath> --target <appDir> --exe <exeName> [--pid <pid>]");
    return 1;
}

options.TryGetValue("pid", out var pidStr);
var logPath = Path.Combine(targetDir, "update.log");

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
    Console.WriteLine(line);
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
}

try
{
    // 1. 等待主进程退出
    if (int.TryParse(pidStr, out var pid))
    {
        Log($"等待主进程退出 (PID={pid})...");
        try
        {
            var proc = Process.GetProcessById(pid);
            if (!proc.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                Log("警告: 主进程 30 秒内未退出，继续更新...");
            }
        }
        catch (ArgumentException)
        {
            // 进程已退出
        }
    }
    else
    {
        // 没有 PID 参数，等待一小段时间确保主进程已退出
        Thread.Sleep(2000);
    }

    // 2. 验证 zip 存在
    if (!File.Exists(zipPath))
    {
        Log($"错误: 更新包不存在: {zipPath}");
        return 1;
    }

    // 3. 创建备份
    var backupDir = Path.Combine(targetDir, "_update_backup");
    if (Directory.Exists(backupDir))
        Directory.Delete(backupDir, true);
    Directory.CreateDirectory(backupDir);

    Log("备份当前文件...");
    BackupFiles(targetDir, backupDir);

    // 4. 解压覆盖
    Log("解压更新包...");
    try
    {
        // zip 内可能有一层子目录，也可能直接是文件
        using var archive = ZipFile.OpenRead(zipPath);
        var hasRootFolder = DetectRootFolder(archive);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // 跳过目录条目

            var relativePath = entry.FullName;
            if (hasRootFolder != null)
            {
                // 剥掉 zip 里的顶层目录前缀
                relativePath = relativePath[(hasRootFolder.Length)..];
            }

            // 不覆盖自身（Updater.exe）
            if (relativePath.Equals("Updater.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            var destPath = Path.Combine(targetDir, relativePath);
            var destDir = Path.GetDirectoryName(destPath)!;
            Directory.CreateDirectory(destDir);
            entry.ExtractToFile(destPath, overwrite: true);
        }

        Log("更新完成。");
    }
    catch (Exception ex)
    {
        // 5. 回滚
        Log($"解压失败: {ex.Message}，正在回滚...");
        RestoreBackup(backupDir, targetDir);
        Log("已回滚到更新前状态。");
        return 1;
    }

    // 6. 清理
    try
    {
        Directory.Delete(backupDir, true);
        File.Delete(zipPath);
    }
    catch
    {
        // 清理失败不影响更新结果
    }

    // 7. 重启主程序
    var exePath = Path.Combine(targetDir, exeName + ".exe");
    if (File.Exists(exePath))
    {
        Log($"启动 {exeName}...");
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = targetDir,
            UseShellExecute = true
        });
    }
    else
    {
        // FDD 场景：可能只有 .dll，尝试通过 dotnet 启动
        var dllPath = Path.Combine(targetDir, exeName + ".dll");
        if (File.Exists(dllPath))
        {
            Log($"通过 dotnet 启动 {exeName}.dll...");
            Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dllPath}\"",
                WorkingDirectory = targetDir,
                UseShellExecute = true
            });
        }
        else
        {
            Log($"警告: 未找到主程序: {exePath} 或 {dllPath}");
        }
    }

    return 0;
}
catch (Exception ex)
{
    Log($"更新器异常: {ex}");
    return 1;
}

// ─── 辅助方法 ───

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i].StartsWith("--"))
        {
            dict[argv[i][2..]] = argv[i + 1];
            i++;
        }
    }
    return dict;
}

static void BackupFiles(string sourceDir, string backupDir)
{
    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(sourceDir, file);
        // 跳过备份目录自身和日志
        if (relativePath.StartsWith("_update_backup", StringComparison.OrdinalIgnoreCase))
            continue;
        if (relativePath.Equals("update.log", StringComparison.OrdinalIgnoreCase))
            continue;
        try
        {
            var destPath = Path.Combine(backupDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
        catch
        {
            // Updater.exe 自身可能被锁定，跳过
        }
    }
}

static void RestoreBackup(string backupDir, string targetDir)
{
    foreach (var file in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(backupDir, file);
        try
        {
            var destPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }
        catch { }
    }
}

/// <summary>
/// 检测 zip 是否所有条目都在同一个顶层目录下。
/// 如果是，返回那个目录前缀（含尾部 /）；否则返回 null。
/// </summary>
static string? DetectRootFolder(ZipArchive archive)
{
    string? root = null;
    foreach (var entry in archive.Entries)
    {
        var parts = entry.FullName.Split('/');
        if (parts.Length < 2) return null; // 有直接在根级的文件
        var candidate = parts[0] + "/";
        if (root == null) root = candidate;
        else if (!root.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return null;
    }
    return root;
}
