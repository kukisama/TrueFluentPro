using System.Net.Http.Headers;
using System.Text.Json;

namespace GptImageCli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
    {
        var json = args.Any(a => a.Split('=')[0].Equals("--json", StringComparison.OrdinalIgnoreCase));
        var report = new CliReport();
        int exit;
        try
        {
            if (args.Length == 1 && args[0] is "-h" or "help") args = ["--help"];
            CommandLineParser.ValidateSyntax(args);
            exit = await RunCoreAsync(args, json ? error : output, error, report, handler);
        }
        catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException or
            ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
        {
            exit = report.Mode is null ? 2 : 1;
            await error.WriteLineAsync($"错误: {ex.Message}");
        }
        if (json) await output.WriteLineAsync(report.Serialize(exit));
        return exit;
    }

    private static async Task<int> RunCoreAsync(string[] args, TextWriter output, TextWriter error,
        CliReport report, HttpMessageHandler? handler)
    {
        if (args.Length == 0 || args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase)))
        {
            Usage.Write(output);
            return 0;
        }

        CliOptions options;
        try
        {
            options = await CommandLineParser.ParseAsync(args);
            report.Mode = options.Mode.ToString().ToLowerInvariant();
            ImageResultWriter.ValidateOutputTargets(options);
        }
        catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            await error.WriteLineAsync($"错误: {ex.Message}");
            await error.WriteLineAsync("运行 `gpt-image --help` 查看用法。");
            return 2;
        }

        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes);
            using var request = RequestFactory.Create(options);
            ApplyAuthentication(request, options);

            await output.WriteLineAsync($"POST {request.RequestUri}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            report.CaptureHeaders(response);
            var responseText = await response.Content.ReadAsStringAsync();
            report.CaptureBody(responseText, options.ApiKey);

            if (!response.IsSuccessStatusCode)
            {
                report.Error = $"HTTP {(int)response.StatusCode}：请求失败。";
                await error.WriteLineAsync($"请求失败: HTTP {(int)response.StatusCode}；服务端正文已省略。");
                return 1;
            }

            var images = ImageResponseParser.Parse(responseText).ToList();
            if (images.Count == 0)
            {
                report.Error = "请求成功，但响应里没有找到图片数据。";
                await error.WriteLineAsync("请求成功，但响应里没有找到图片数据。");
                return 1;
            }

            var savedFiles = await ImageResultWriter.SaveAsync(images, options.OutputPath, options.OutputFormat, client, report.Files, options.Overwrite);
            foreach (var file in savedFiles)
            {
                await output.WriteLineAsync(file);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("请求已取消或超时。");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"网络请求失败: {ex.Message}");
            return 1;
        }
        catch (JsonException)
        {
            await error.WriteLineAsync("响应 JSON 解析失败；服务端正文已省略。");
            return 1;
        }
        catch (IOException ex)
        {
            await error.WriteLineAsync($"文件读取或写入失败: {ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            await error.WriteLineAsync($"没有权限读取或写入文件: {ex.Message}");
            return 1;
        }
    }

    private static void ApplyAuthentication(HttpRequestMessage request, CliOptions options)
    {
        if (options.AuthMode == AuthMode.ApiKey)
        {
            request.Headers.TryAddWithoutValidation("api-key", options.ApiKey);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }
}
