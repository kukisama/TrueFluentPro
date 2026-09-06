using System;
using System.Diagnostics;
using System.IO;

namespace TrueFluentPro.Services;

public interface IDesktopFolderLauncher
{
    void Open(string directoryPath);
}

/// <summary>使用操作系统默认文件管理器打开本地目录。</summary>
public sealed class DesktopFolderLauncher : IDesktopFolderLauncher
{
    public void Open(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("目录路径为空。", nameof(directoryPath));

        var fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"目录不存在：{fullPath}");

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }
}
