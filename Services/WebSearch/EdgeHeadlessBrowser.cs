using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;

namespace TrueFluentPro.Services.WebSearch;

/// <summary>
/// 用系统自带 Edge + PuppeteerSharp (CDP) 获取 JS 渲染后的完整 HTML。
/// 浏览器进程常驻复用，每次请求只开新 Page，性能远优于每次 Process.Start。
/// </summary>
public static class EdgeHeadlessBrowser
{
    private static readonly string? EdgePath = FindEdge();
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static IBrowser? _browser;

    /// <summary>是否可用（系统安装了 Edge）</summary>
    public static bool IsAvailable => EdgePath is not null;

    /// <summary>
    /// 用 PuppeteerSharp + 系统 Edge 获取渲染后的 HTML。
    /// 首次调用启动 Edge 进程（~1s），后续每页 ~200ms。
    /// </summary>
    public static async Task<string?> GetRenderedHtmlAsync(string url, CancellationToken ct = default, int timeoutMs = 15_000)
    {
        if (EdgePath is null) return null;

        var browser = await GetOrCreateBrowserAsync();
        if (browser is null) return null;

        await using var page = await browser.NewPageAsync();
        try
        {
            // 禁用图片加载，加速页面渲染
            await page.SetRequestInterceptionAsync(true);
            page.Request += async (_, e) =>
            {
                if (e.Request.ResourceType is ResourceType.Image or ResourceType.Media or ResourceType.Font)
                    await e.Request.AbortAsync();
                else
                    await e.Request.ContinueAsync();
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            // WaitUntilNavigation.DOMContentLoaded 比 NetworkIdle 更快、更可靠
            var response = await page.GoToAsync(url, new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                Timeout = timeoutMs
            });

            var html = await page.GetContentAsync();
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or NavigationException)
        {
            return null;
        }
    }

    /// <summary>获取或懒启动浏览器实例（线程安全）</summary>
    private static async Task<IBrowser?> GetOrCreateBrowserAsync()
    {
        if (_browser is { IsClosed: false }) return _browser;

        await InitLock.WaitAsync();
        try
        {
            if (_browser is { IsClosed: false }) return _browser;

            _browser = await Puppeteer.LaunchAsync(new LaunchOptions
            {
                Headless = true,
                ExecutablePath = EdgePath,
                Args = ["--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage"]
            });
            return _browser;
        }
        catch
        {
            return null;
        }
        finally
        {
            InitLock.Release();
        }
    }

    /// <summary>应用退出时关闭浏览器进程</summary>
    public static async Task ShutdownAsync()
    {
        if (_browser is { IsClosed: false })
        {
            await _browser.CloseAsync();
            _browser.Dispose();
            _browser = null;
        }
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
