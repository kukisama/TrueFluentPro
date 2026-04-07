using System.Runtime.InteropServices;
using System.Text;

namespace IconTool.Core;

/// <summary>
/// 设置目录自定义图标（通过 Shell API + desktop.ini + Shell 通知）。
/// </summary>
public static class DirectoryIconService
{
    /// <summary>
    /// 将指定的 ICO 文件设置为目标目录的图标。
    /// </summary>
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

        if (File.Exists(destIcoPath))
            File.SetAttributes(destIcoPath, FileAttributes.Normal);

        var sourceFullPath = Path.GetFullPath(icoFilePath);
        if (!string.Equals(sourceFullPath, destIcoPath, StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceFullPath, destIcoPath, overwrite: true);

        File.SetAttributes(destIcoPath, FileAttributes.Hidden | FileAttributes.System);

        ApplyFolderIcon(targetDirectory, icoFileName);
    }

    /// <summary>
    /// 将 ICO 字节数据保存到目标目录并设置为目录图标。
    /// </summary>
    public static void SetDirectoryIconFromBytes(byte[] icoData, string icoFileName, string targetDirectory)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(targetDirectory))
            throw new DirectoryNotFoundException($"目录不存在: {targetDirectory}");

        if (!icoFileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            icoFileName += ".ico";

        var destIcoPath = Path.Combine(targetDirectory, icoFileName);

        if (File.Exists(destIcoPath))
            File.SetAttributes(destIcoPath, FileAttributes.Normal);

        File.WriteAllBytes(destIcoPath, icoData);
        File.SetAttributes(destIcoPath, FileAttributes.Hidden | FileAttributes.System);

        ApplyFolderIcon(targetDirectory, icoFileName);
    }

    /// <summary>
    /// 核心流程：设置文件夹图标并刷新 Shell 缓存。
    /// </summary>
    private static void ApplyFolderIcon(string targetDirectory, string icoFileName)
    {
        // Step 1: 使用 Shell API 设置文件夹图标
        // 这是 Explorer "属性→自定义→更改图标" 使用的同一 API
        // 它会自动写入 desktop.ini (IconFile/IconIndex) 并设置系统文件夹属性
        int hr = ShellNotify.SetFolderIcon(targetDirectory, icoFileName, 0);

        if (hr < 0)
        {
            // Shell API 失败时，手动写入 desktop.ini 作为回退
            WriteDesktopIni(targetDirectory, icoFileName);
        }

        // Step 2: 补充 IconResource 条目（Shell API 只写 IconFile/IconIndex 旧格式）
        // IconResource 是 Vista+ 的新格式，支持高清图标
        ShellNotify.WriteIniEntry(targetDirectory, "IconResource", $"{icoFileName},0");

        // Step 3: 确保 desktop.ini 属性正确
        var desktopIniPath = Path.Combine(targetDirectory, "desktop.ini");
        if (File.Exists(desktopIniPath))
            File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);

        // Step 4: 确保系统文件夹属性
        ShellNotify.MakeSystemFolder(targetDirectory);

        // Step 5: 通知 Shell 刷新
        NotifyShellIconChanged(targetDirectory);
    }

    private static void WriteDesktopIni(string targetDirectory, string icoFileName)
    {
        var desktopIniPath = Path.Combine(targetDirectory, "desktop.ini");

        if (File.Exists(desktopIniPath))
        {
            var existingAttr = File.GetAttributes(desktopIniPath);
            File.SetAttributes(desktopIniPath,
                existingAttr & ~(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly));
        }

        var iniContent = $"[.ShellClassInfo]\r\nIconResource={icoFileName},0\r\n";
        File.WriteAllText(desktopIniPath, iniContent, Encoding.Unicode);

        File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);
    }

    private static void NotifyShellIconChanged(string directoryPath)
    {
        try
        {
            var desktopIniPath = Path.Combine(directoryPath, "desktop.ini");

            // Layer 1: 通知 desktop.ini 文件变更（最精确的通知）
            ShellNotify.NotifyUpdateItem(desktopIniPath);

            // Layer 2: 通知目录本身更新
            ShellNotify.NotifyUpdateDir(directoryPath);

            // Layer 3: 通知父目录刷新
            var parent = Path.GetDirectoryName(directoryPath);
            if (parent is not null)
                ShellNotify.NotifyUpdateDir(parent);

            // Layer 4: 全局关联变更 — 强制图标/缩略图缓存失效
            // 使用 FLUSHNOWAIT 避免同步等待所有 Shell 扩展响应引起卡顿
            ShellNotify.NotifyAssocChanged();

            // Layer 5: 重初始化系统图像列表
            ShellNotify.ReinitializeIconCache();

            // Layer 6: 广播 WM_SETTINGCHANGE
            ShellNotify.BroadcastSettingChange();
        }
        catch
        {
            // 非致命，忽略
        }
    }
}

/// <summary>
/// Shell 通知 P/Invoke 封装。
/// 使用 SHGetSetFolderCustomSettings（Explorer 自身使用的 API）+ 多层 SHChangeNotify 通知。
/// </summary>
internal static
#if NET7_0_OR_GREATER
    partial
#endif
    class ShellNotify
{
    // ─── SHChangeNotify 常量 ───
    private const int SHCNE_UPDATEITEM = 0x00002000;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    private const int SHCNF_PATHW = 0x0005;
    private const int SHCNF_IDLIST = 0x0000;
    private const int SHCNF_FLUSH = 0x1000;
    private const int SHCNF_FLUSHNOWAIT = 0x3000; // 异步投递但保证入队

    // ─── SHGetSetFolderCustomSettings 常量 ───
    private const uint FCSM_ICONFILE = 0x00000010;
    private const uint FCS_FORCEWRITE = 0x00000002;

    // ─── WM_SETTINGCHANGE ───
    private const int HWND_BROADCAST = 0xFFFF;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    // ─── P/Invoke: shell32.dll ───

#if NET7_0_OR_GREATER
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(int wEventId, int uFlags, string? dwItem1, string? dwItem2);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    // ─── P/Invoke: shlwapi.dll ───

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PathMakeSystemFolderW(string pszPath);

    // ─── P/Invoke: user32.dll ───

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(
        IntPtr hWnd, int msg, IntPtr wParam, string? lParam,
        int fuFlags, int uTimeout, out IntPtr lpdwResult);
#else
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, int uFlags, string? dwItem1, string? dwItem2);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PathMakeSystemFolderW(string pszPath);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, int msg, IntPtr wParam, string? lParam,
        int fuFlags, int uTimeout, out IntPtr lpdwResult);
#endif

    // ─── P/Invoke: shell32.dll (DllImport — 复杂结构体需传统 Marshaller) ───

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize;
        public uint dwMask;
        public IntPtr pvid;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszWebViewTemplate;
        public uint cchWebViewTemplate;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszWebViewTemplateVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszLogo;
        public uint cchLogo;
    }

#pragma warning disable SYSLIB1054 // 复杂结构体需要 DllImport 的自动 Marshalling
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetSetFolderCustomSettings(
        ref SHFOLDERCUSTOMSETTINGS pfcs, string pszPath, uint dwReadWrite);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrivateProfileString(
        string lpAppName, string lpKeyName, string lpString, string lpFileName);
#pragma warning restore SYSLIB1054

    // ─── 公共方法 ───

    /// <summary>
    /// 使用 Shell API 设置文件夹图标（与 Explorer "属性→自定义→更改图标" 相同代码路径）。
    /// 返回 HRESULT（0 = 成功，负值 = 失败）。
    /// </summary>
    public static int SetFolderIcon(string folderPath, string icoFileName, int iconIndex)
    {
        var settings = new SHFOLDERCUSTOMSETTINGS
        {
            dwSize = (uint)Marshal.SizeOf<SHFOLDERCUSTOMSETTINGS>(),
            dwMask = FCSM_ICONFILE,
            pszIconFile = icoFileName,
            cchIconFile = 0,
            iIconIndex = iconIndex
        };

        return SHGetSetFolderCustomSettings(ref settings, folderPath, FCS_FORCEWRITE);
    }

    /// <summary>
    /// 使用 WritePrivateProfileString 在 desktop.ini 中写入/更新指定条目。
    /// 不会覆盖其他已有条目。
    /// </summary>
    public static void WriteIniEntry(string folderPath, string key, string value)
    {
        var desktopIniPath = Path.Combine(folderPath, "desktop.ini");
        WritePrivateProfileString(".ShellClassInfo", key, value, desktopIniPath);
    }

    /// <summary>
    /// 使用 PathMakeSystemFolder (shlwapi.dll) 将目录标记为系统文件夹。
    /// </summary>
    public static void MakeSystemFolder(string directoryPath)
    {
        PathMakeSystemFolderW(directoryPath);
    }

    /// <summary>
    /// 通知 Shell 某个文件已变更，同步等待处理完成。
    /// </summary>
    public static void NotifyUpdateItem(string filePath)
    {
        SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSH, filePath, null);
    }

    /// <summary>
    /// 通知 Shell 某个目录内容已变更，同步等待处理完成。
    /// </summary>
    public static void NotifyUpdateDir(string directoryPath)
    {
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, directoryPath, null);
    }

    /// <summary>
    /// 通知 Shell 文件关联已改变 — 强制图标和缩略图缓存失效。
    /// 使用 FLUSHNOWAIT 避免同步等待所有 Shell 扩展响应导致卡顿。
    /// </summary>
    public static void NotifyAssocChanged()
    {
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 重初始化系统图像列表（FileIconInit，Shell32.dll ordinal 660）。
    /// </summary>
    public static void ReinitializeIconCache()
    {
        try
        {
#if NET5_0_OR_GREATER
            var shell32 = NativeLibrary.Load("shell32.dll");
            if (NativeLibrary.TryGetExport(shell32, "#660", out var funcPtr))
            {
                var fileIconInit = Marshal.GetDelegateForFunctionPointer<FileIconInitDelegate>(funcPtr);
                fileIconInit(false);
                fileIconInit(true);
            }
#else
            var shell32 = LoadLibrary("shell32.dll");
            if (shell32 != IntPtr.Zero)
            {
                var funcPtr = GetProcAddress(shell32, "#660");
                if (funcPtr != IntPtr.Zero)
                {
                    var fileIconInit = Marshal.GetDelegateForFunctionPointer<FileIconInitDelegate>(funcPtr);
                    fileIconInit(false);
                    fileIconInit(true);
                }
            }
#endif
        }
        catch
        {
            // 非致命
        }
    }

    /// <summary>
    /// 广播 WM_SETTINGCHANGE。
    /// </summary>
    public static void BroadcastSettingChange()
    {
        try
        {
            SendMessageTimeoutW(
                (IntPtr)HWND_BROADCAST,
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "Environment",
                SMTO_ABORTIFHUNG,
                3000,
                out _);
        }
        catch
        {
            // 非致命
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool FileIconInitDelegate([MarshalAs(UnmanagedType.Bool)] bool fRestoreCache);
}
