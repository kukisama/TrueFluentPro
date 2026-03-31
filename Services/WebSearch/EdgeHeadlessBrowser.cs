using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 用系统自带 Edge 的 headless 模式获取 JS 渲染后的完整 HTML。
/// 等效 Cherry Studio 的 Electron BrowserWindow + executeJavaScript('document.documentElement.outerHTML')。
/// Windows 系统均预装 Edge，无需额外依赖。
/// </summary>
public static class EdgeHeadlessBrowser
{
    /// <summary>Edge 可执行文件路径（启动时探测一次）</summary>
    private static readonly string? EdgePath = FindEdge();

    /// <summary>是否可用（系统安装了 Edge）</summary>
    public static bool IsAvailable => EdgePath is not null;

    /// <summary>
    /// 用 Edge headless --dump-dom 获取渲染后的 HTML。
    /// 典型耗时 2-3 秒（首次），后续可能更快。
    /// </summary>
    public static async Task<string?> GetRenderedHtmlAsync(string url, CancellationToken ct = default, int timeoutMs = 15_000)
    {
        if (EdgePath is null) return null;

        var psi = new ProcessStartInfo
        {
            FileName = EdgePath,
            // --headless: 无头模式
            // --disable-gpu: 无需 GPU
            // --dump-dom: 渲染完成后输出 DOM 到 stdout
            // --no-sandbox: 避免沙箱权限问题
            // --blink-settings=imagesEnabled=false: 跳过图片请求，加速加载
            Arguments = $"--headless --disable-gpu --no-sandbox --blink-settings=imagesEnabled=false --dump-dom \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            var html = await process.StandardOutput.ReadToEndAsync(cts.Token);
            // 不等 stderr，直接等进程结束
            await process.WaitForExitAsync(cts.Token);
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>按优先级查找 Edge 路径</summary>
    private static string? FindEdge()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            // 用户安装
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
