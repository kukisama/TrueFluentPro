using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    /// <summary>
    /// 通过 GitHub Releases API 检查应用更新、下载更新包，并调用外部 Updater.exe 完成替换。
    /// 所有网络操作均有超时保护，失败时静默返回 null，不影响主流程。
    /// </summary>
    public sealed class UpdateService
    {
        private const string RepoOwner = "kukisama";
        private const string RepoName = "TrueFluentPro";
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TrueFluentPro-UpdateChecker/1.0");
            return client;
        }

        /// <summary>
        /// 检查是否有新版本。网络不可达时返回 null（静默失败）。
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken ct = default)
        {
            try
            {
                var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
                using var response = await Http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode)
                    return null;

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var versionStr = tagName.TrimStart('v');
                if (!Version.TryParse(versionStr, out var remoteVersion))
                    return null;

                if (remoteVersion <= currentVersion)
                    return null;

                var htmlUrl = root.GetProperty("html_url").GetString() ?? "";
                var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";

                // 选择匹配当前架构的 asset
                var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                var downloadUrl = "";
                long assetSize = 0;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.Contains("TrueFluentPro", StringComparison.OrdinalIgnoreCase)
                            && name.Contains(rid, StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            assetSize = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;
                            break;
                        }
                    }
                }

                return new UpdateInfo
                {
                    LatestVersion = remoteVersion,
                    DownloadUrl = downloadUrl,
                    ReleasePageUrl = htmlUrl,
                    ReleaseNotes = body,
                    AssetSize = assetSize
                };
            }
            catch
            {
                // 网络超时、DNS 解析失败、JSON 解析异常等 → 静默返回 null
                return null;
            }
        }

        /// <summary>
        /// 下载更新 zip 到临时目录，返回本地文件路径。
        /// 如果对应文件已存在且大小与 expectedSize 匹配，跳过下载直接返回。
        /// </summary>
        public async Task<string?> DownloadUpdateAsync(string downloadUrl, long expectedSize = 0, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "TrueFluentPro_Update");
                Directory.CreateDirectory(tempDir);
                var fileName = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
                var filePath = Path.Combine(tempDir, fileName);

                // 利用 GitHub API 返回的 asset size 判断缓存是否有效，无需额外 HTTP 请求
                if (expectedSize > 0 && File.Exists(filePath) && new FileInfo(filePath).Length == expectedSize)
                {
                    progress?.Report(1.0);
                    return filePath; // 已缓存，跳过下载
                }

                using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalRead += bytesRead;
                    if (totalBytes > 0)
                        progress?.Report((double)totalRead / totalBytes);
                }

                return filePath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 启动外部 Updater.exe 执行替换，然后退出当前进程。
        /// </summary>
        public bool LaunchUpdaterAndExit(string zipPath)
        {
            // TrimEnd 防止尾部 \ 在引号内被解析为转义符（"C:\foo\" → \" 被当作转义引号）
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var updaterPath = Path.Combine(appDir, "Updater.exe");

            if (!File.Exists(updaterPath))
                return false;

            var exeName = Process.GetCurrentProcess().ProcessName;
            // Updater 参数：--zip <zipPath> --target <appDir> --exe <exeName> --pid <pid>
            var args = $"--zip \"{zipPath}\" --target \"{appDir}\" --exe \"{exeName}\" --pid {Environment.ProcessId}";

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            // 退出主程序，让 Updater 完成替换
            Environment.Exit(0);
            return true; // 不会实际到达
        }

        /// <summary>
        /// 从 RELEASE_NOTES.md 解析当前版本号。
        /// </summary>
        public static Version ParseCurrentVersion()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "RELEASE_NOTES.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "RELEASE_NOTES.md")
            };

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var firstLine = File.ReadLines(path).FirstOrDefault() ?? "";
                var match = Regex.Match(firstLine, @"v(\d+\.\d+\.\d+)");
                if (match.Success && Version.TryParse(match.Groups[1].Value, out var ver))
                    return ver;
            }

            return new Version(0, 0, 0);
        }
    }
}
