using System.Runtime.InteropServices;
using System.Text;

namespace IconTool.Core;

/// <summary>
/// 设置目录自定义图标（通过 desktop.ini 和 Shell 通知）。
/// </summary>
public static class DirectoryIconService
{
    /// <summary>
    /// 将指定的 ICO 文件设置为目标目录的图标。
    /// 自动复制 ICO、创建/更新 desktop.ini、设置文件属性、通知 Shell 刷新。
    /// </summary>
    /// <param name="icoFilePath">ICO 文件路径</param>
    /// <param name="targetDirectory">目标目录</param>
    public static void SetDirectoryIcon(string icoFilePath, string targetDirectory)
    {
        if (!File.Exists(icoFilePath))
            throw new FileNotFoundException($"ICO 文件不存在: {icoFilePath}", icoFilePath);

        if (!Path.GetExtension(icoFilePath).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("图标文件必须是 .ico 格式。");

        targetDirectory = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException($"目录不存在: {targetDirectory}");

        var icoFileName = Path.GetFileName(icoFilePath);
        var destIcoPath = Path.Combine(targetDirectory, icoFileName);

        // 复制 ICO 到目标目录（如果不在同一位置）
        var sourceFullPath = Path.GetFullPath(icoFilePath);
        if (!string.Equals(sourceFullPath, destIcoPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceFullPath, destIcoPath, overwrite: true);
        }

        // 设置 ICO 文件为隐藏+系统
        File.SetAttributes(destIcoPath, FileAttributes.Hidden | FileAttributes.System);

        // 创建/更新 desktop.ini
        WriteDesktopIni(targetDirectory, icoFileName);

        // 设置目标目录为 ReadOnly（Shell 要求此属性才会读取 desktop.ini）
        var dirInfo = new DirectoryInfo(targetDirectory);
        dirInfo.Attributes |= FileAttributes.ReadOnly;

        // 通知 Shell 刷新
        NotifyShellIconChanged(targetDirectory);
    }

    /// <summary>
    /// 将 ICO 字节数据保存到目标目录并设置为目录图标。
    /// </summary>
    /// <param name="icoData">ICO 文件字节</param>
    /// <param name="icoFileName">ICO 文件名（如 "myicon.ico"）</param>
    /// <param name="targetDirectory">目标目录</param>
    public static void SetDirectoryIconFromBytes(byte[] icoData, string icoFileName, string targetDirectory)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException($"目录不存在: {targetDirectory}");

        if (!icoFileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            icoFileName += ".ico";

        var destIcoPath = Path.Combine(targetDirectory, icoFileName);
        File.WriteAllBytes(destIcoPath, icoData);

        // 设置 ICO 文件为隐藏+系统
        File.SetAttributes(destIcoPath, FileAttributes.Hidden | FileAttributes.System);

        // 创建/更新 desktop.ini
        WriteDesktopIni(targetDirectory, icoFileName);

        // 设置目标目录为 ReadOnly
        var dirInfo = new DirectoryInfo(targetDirectory);
        dirInfo.Attributes |= FileAttributes.ReadOnly;

        // 通知 Shell 刷新
        NotifyShellIconChanged(targetDirectory);
    }

    private static void WriteDesktopIni(string targetDirectory, string icoFileName)
    {
        var desktopIniPath = Path.Combine(targetDirectory, "desktop.ini");

        // 先移除只读属性以便写入
        if (File.Exists(desktopIniPath))
        {
            var existingAttr = File.GetAttributes(desktopIniPath);
            File.SetAttributes(desktopIniPath,
                existingAttr & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
        }

        var iniContent = $"[.ShellClassInfo]\nIconResource={icoFileName},0\n";
        File.WriteAllText(desktopIniPath, iniContent, Encoding.UTF8);

        // 设置 desktop.ini 为隐藏+系统
        File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);
    }

    private static void NotifyShellIconChanged(string directoryPath)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            ShellNotify.UpdateDirectoryIcon(directoryPath);
        }
        catch
        {
            // 非致命，忽略
        }
    }

}

/// <summary>
/// Shell 通知 P/Invoke 封装。
/// </summary>
internal static partial class ShellNotify
{
    // SHCNE_UPDATEDIR = 0x00001000, SHCNF_PATHW = 0x0005
    // SHCNE_ASSOCCHANGED = 0x08000000, SHCNF_IDLIST = 0x0000
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int wEventId, int uFlags, string? dwItem1, string? dwItem2);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    public static void UpdateDirectoryIcon(string directoryPath)
    {
        SHChangeNotify(0x00001000, 0x0005, directoryPath, null);
        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }
}
