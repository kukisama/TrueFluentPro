namespace GptImageCli;

internal sealed class CliException(string message) : Exception(message);

internal static class CommandLineParser
{
    public static async Task<CliOptions> ParseAsync(string[] args)
    {
        var values = ParseKeyValues(args);

        var endpoint = FirstNonEmpty(Get(values, "endpoint"), Environment.GetEnvironmentVariable("GPT_IMAGE_ENDPOINT"), Environment.GetEnvironmentVariable("OPENAI_BASE_URL"), Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"));
        var apiKey = FirstNonEmpty(Get(values, "api-key"), Environment.GetEnvironmentVariable("GPT_IMAGE_API_KEY"), Environment.GetEnvironmentVariable("OPENAI_API_KEY"), Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
        var prompt = await ResolvePromptAsync(values);

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new CliException("缺少 --endpoint，或设置 GPT_IMAGE_ENDPOINT / OPENAI_BASE_URL / AZURE_OPENAI_ENDPOINT。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CliException("缺少 --api-key，或设置 GPT_IMAGE_API_KEY / OPENAI_API_KEY / AZURE_OPENAI_API_KEY。");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new CliException("缺少 --prompt / --prompt-file，或通过 stdin 输入提示词。");

        var mode = ParseMode(Get(values, "mode"));
        var auth = ParseAuth(Get(values, "auth"), endpoint);
        var count = ParsePositiveInt(Get(values, "n") ?? Get(values, "count"), 1, "n");
        var timeout = ParsePositiveInt(Get(values, "timeout-minutes"), 10, "timeout-minutes");
        var outputFormat = NormalizeFormat(Get(values, "format") ?? Get(values, "output-format") ?? "png");

        return new CliOptions
        {
            Endpoint = endpoint.Trim(),
            ApiKey = apiKey.Trim(),
            Prompt = prompt.Trim(),
            Mode = mode,
            AuthMode = auth,
            TextModel = Get(values, "model") ?? Environment.GetEnvironmentVariable("GPT_IMAGE_TEXT_MODEL") ?? "gpt-4.1",
            ImageModel = Get(values, "image-model") ?? Environment.GetEnvironmentVariable("GPT_IMAGE_MODEL") ?? "gpt-image-2",
            ApiVersion = Get(values, "api-version") ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"),
            Size = Get(values, "size") ?? "1024x1024",
            Quality = Get(values, "quality") ?? "medium",
            OutputFormat = outputFormat,
            Count = count,
            OutputPath = Get(values, "output") ?? Get(values, "out") ?? ".",
            TimeoutMinutes = timeout
        };
    }

    private static Dictionary<string, string?> ParseKeyValues(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new CliException($"无法识别参数 `{arg}`。");

            var keyValue = arg[2..];
            var equalsIndex = keyValue.IndexOf('=');
            if (equalsIndex >= 0)
            {
                values[keyValue[..equalsIndex]] = keyValue[(equalsIndex + 1)..];
                continue;
            }

            var key = keyValue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[key] = args[++i];
            }
            else
            {
                values[key] = "true";
            }
        }

        return values;
    }

    private static async Task<string?> ResolvePromptAsync(Dictionary<string, string?> values)
    {
        var prompt = Get(values, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            return prompt;

        var promptFile = Get(values, "prompt-file");
        if (!string.IsNullOrWhiteSpace(promptFile))
            return await File.ReadAllTextAsync(promptFile);

        if (Console.IsInputRedirected)
            return await Console.In.ReadToEndAsync();

        return null;
    }

    private static ApiMode ParseMode(string? value) => (value ?? "responses").ToLowerInvariant() switch
    {
        "responses" or "response" => ApiMode.Responses,
        "images" or "image" or "generations" => ApiMode.Images,
        _ => throw new CliException("--mode 只支持 responses 或 images。")
    };

    private static AuthMode ParseAuth(string? value, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return endpoint.Contains("openai.azure.com", StringComparison.OrdinalIgnoreCase) ? AuthMode.ApiKey : AuthMode.Bearer;

        return value.ToLowerInvariant() switch
        {
            "bearer" => AuthMode.Bearer,
            "api-key" or "apikey" => AuthMode.ApiKey,
            _ => throw new CliException("--auth 只支持 auto、bearer 或 api-key。")
        };
    }

    private static int ParsePositiveInt(string? value, int defaultValue, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new CliException($"--{optionName} 必须是正整数。");
        return parsed;
    }

    private static string NormalizeFormat(string value) => value.ToLowerInvariant() switch
    {
        "png" or "jpeg" or "jpg" or "webp" => value.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : value.ToLowerInvariant(),
        _ => throw new CliException("--format 只支持 png、jpeg 或 webp。")
    };

    private static string? Get(Dictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
