using System.Net.Http.Headers;
using System.Text.Json;

namespace GptImageCli;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help" or "help"))
        {
            Usage.Write(output);
            return 0;
        }

        CliOptions options;
        try
        {
            options = await CommandLineParser.ParseAsync(args);
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"错误: {ex.Message}");
            await error.WriteLineAsync("运行 `gpt-image --help` 查看用法。");
            return 2;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(options.TimeoutMinutes) };
            using var request = RequestFactory.Create(options);
            ApplyAuthentication(request, options);

            await output.WriteLineAsync($"POST {request.RequestUri}");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                await error.WriteLineAsync($"请求失败: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                await error.WriteLineAsync(responseText);
                return 1;
            }

            var images = ImageResponseParser.Parse(responseText).ToList();
            if (images.Count == 0)
            {
                await error.WriteLineAsync("请求成功，但响应里没有找到图片数据。");
                await error.WriteLineAsync(responseText);
                return 1;
            }

            var savedFiles = await ImageResultWriter.SaveAsync(images, options.OutputPath, options.OutputFormat, client);
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
        catch (JsonException ex)
        {
            await error.WriteLineAsync($"响应 JSON 解析失败: {ex.Message}");
            return 1;
        }
        catch (IOException ex)
        {
            await error.WriteLineAsync($"文件写入失败: {ex.Message}");
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
