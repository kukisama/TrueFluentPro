namespace GptImageCli;

internal sealed class CliException(string message) : Exception(message);

internal static class CommandLineParser
{
    public static async Task<CliOptions> ParseAsync(string[] args)
    {
        var referenceImages = new List<string>();
        var values = ParseKeyValues(args, referenceImages);

        var endpoint = FirstNonEmpty(Get(values, "endpoint"), Environment.GetEnvironmentVariable("GPT_IMAGE_ENDPOINT"), Environment.GetEnvironmentVariable("OPENAI_BASE_URL"), Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"));
        var explicitApiKey = Get(values, "api-key");
        var environmentApiKey = FirstNonEmpty(Environment.GetEnvironmentVariable("GPT_IMAGE_API_KEY"), Environment.GetEnvironmentVariable("OPENAI_API_KEY"), Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
        var apiKey = explicitApiKey ?? environmentApiKey;
        // 外部密钥必须有明确目标；选择器表示用户授权将该密钥覆盖到所选节点。
        // 不能自动选配置目标，也不能悄悄丢弃外部密钥改用配置密钥。
        if (apiKey is not null && endpoint is null && Get(values, "endpoint-name") is null && Get(values, "endpoint-id") is null)
            throw new CliException($"{(explicitApiKey is not null ? "显式 --api-key" : "环境密钥")}缺少明确目标；请提供 --endpoint、环境 URL 或 --endpoint-name / --endpoint-id。未发送请求。");
        var imageModel = FirstNonEmpty(Get(values, "image-model"), Environment.GetEnvironmentVariable("GPT_IMAGE_MODEL"));
        var mode = ParseMode(Get(values, "mode"));
        ConfigConnection? connection = null;
        if (!values.ContainsKey("no-config") && (endpoint is null || apiKey is null || Get(values, "endpoint-name") is not null || Get(values, "endpoint-id") is not null))
            connection = LocalEndpointConfig.Resolve(Get(values, "config"), Get(values, "endpoint-name"), Get(values, "endpoint-id"), endpoint, apiKey, imageModel, mode);
        endpoint ??= connection?.Endpoint;
        apiKey ??= connection?.Key;
        var prompt = await ResolvePromptAsync(values);

        if (string.IsNullOrWhiteSpace(endpoint))
            throw new CliException("缺少 --endpoint，或设置 GPT_IMAGE_ENDPOINT / OPENAI_BASE_URL / AZURE_OPENAI_ENDPOINT。");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new CliException("缺少 --api-key，或设置 GPT_IMAGE_API_KEY / OPENAI_API_KEY / AZURE_OPENAI_API_KEY。");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new CliException("缺少 --prompt / --prompt-file，或通过 stdin 输入提示词。");

        if (mode == ApiMode.Edit && referenceImages.Count == 0)
            throw new CliException("edit 改图模式必须提供 --image <参考图路径>。");
        if (mode != ApiMode.Edit && referenceImages.Count > 0)
            throw new CliException("--image 仅用于 --mode edit；不会忽略参考图退化成文生图。");
        foreach (var path in referenceImages)
        {
            if (!File.Exists(path))
                throw new CliException($"参考图不存在或不是文件：{path}");
            if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                throw new CliException($"参考图只支持 PNG、JPEG 或 WebP：{path}");
        }
        var auth = Get(values, "auth") is { } explicitAuth && !explicitAuth.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? ParseAuth(explicitAuth, endpoint) : connection?.Auth ?? ParseAuth(null, endpoint);
        var count = ParsePositiveInt(Get(values, "n") ?? Get(values, "count"), 1, "n");
        var timeout = ParsePositiveInt(Get(values, "timeout-minutes"), 10, "timeout-minutes");
        var outputFormat = NormalizeFormat(Get(values, "format") ?? Get(values, "output-format") ?? "png");

        var options = new CliOptions
        {
            Endpoint = endpoint.Trim(),
            ApiKey = apiKey.Trim(),
            Prompt = prompt.Trim(),
            Mode = mode,
            ReferenceImagePaths = referenceImages.ToArray(),
            AuthMode = auth,
            TextModel = Get(values, "model") ?? Environment.GetEnvironmentVariable("GPT_IMAGE_TEXT_MODEL") ?? "gpt-4.1",
            ImageModel = connection?.Model ?? imageModel ?? "gpt-image-2",
            LogicalImageModel = connection?.LogicalModel,
            ConfiguredRequestUrl = connection?.RequestUrl,
            ApiVersion = Get(values, "api-version") ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION"),
            Size = Get(values, "size") ?? "1024x640",
            Quality = Get(values, "quality") ?? "medium",
            OutputFormat = outputFormat,
            Count = count,
            OutputPath = Get(values, "output") ?? Get(values, "out") ?? ".",
            Overwrite = values.ContainsKey("overwrite"),
            TimeoutMinutes = timeout,
            MaskPath = Get(values, "mask"),
            Background = Get(values, "background")?.ToLowerInvariant(),
            OutputCompression = Get(values, "output-compression") is { } compression
                ? int.TryParse(compression, out var number) ? number : throw new CliException("--output-compression 必须是 0..100 的整数。") : null,
            Moderation = Get(values, "moderation")?.ToLowerInvariant(),
            User = Get(values, "user")
        };
        ParameterValidation.Validate(options);
        return options;
    }

    internal static void ValidateSyntax(string[] args) => ParseKeyValues(args, []);

    internal static EndpointSummary[]? ListEndpoints(string[] args)
    {
        var values = ParseKeyValues(args, []);
        return values.ContainsKey("list-endpoints") ? LocalEndpointConfig.List(Get(values, "config")) : null;
    }

    private static Dictionary<string, string?> ParseKeyValues(string[] args, List<string> referenceImages)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                throw new CliException($"无法识别参数 `{arg}`。");

            var keyValue = arg[2..];
            var equalsIndex = keyValue.IndexOf('=');
            var option = equalsIndex < 0 ? keyValue : keyValue[..equalsIndex];
            const string allowed = "endpoint endpoint-name endpoint-id list-endpoints config no-config api-key prompt prompt-file mode image auth model image-model api-version size quality format output-format n count output out timeout-minutes mask background output-compression moderation user json help overwrite";
            if (!allowed.Split(' ').Contains(option, StringComparer.OrdinalIgnoreCase))
                throw new CliException($"未知参数 --{option}；未发送请求。");
            if (option.Equals("json", StringComparison.OrdinalIgnoreCase) || option.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("overwrite", StringComparison.OrdinalIgnoreCase) || option.Equals("list-endpoints", StringComparison.OrdinalIgnoreCase) || option.Equals("no-config", StringComparison.OrdinalIgnoreCase))
            {
                if (equalsIndex >= 0 || (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)))
                    throw new CliException($"--{option} 是无值开关。");
                values[option] = "true";
                continue;
            }
            if (equalsIndex >= 0)
            {
                AddValue(keyValue[..equalsIndex], keyValue[(equalsIndex + 1)..]);
                continue;
            }

            var key = keyValue;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                AddValue(key, args[++i]);
            }
            else
            {
                throw new CliException($"--{key} 后必须指定值。");
            }
        }

        if (values.ContainsKey("endpoint-name") && values.ContainsKey("endpoint-id")) throw new CliException("--endpoint-name 与 --endpoint-id 互斥。");
        if (values.ContainsKey("no-config") && new[] { "config", "endpoint-name", "endpoint-id", "list-endpoints" }.Any(values.ContainsKey))
            throw new CliException("--no-config 不能与 --config、节点选择器或 --list-endpoints 同用。");
        return values;

        void AddValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new CliException($"--{key} 后必须指定非空值。");
            if (key.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(value))
                    throw new CliException("--image 后必须指定参考图路径。");
                referenceImages.Add(value);
            }
            else
            {
                values[key] = value;
            }
        }
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

    private static ApiMode ParseMode(string? value) => (value ?? "images").ToLowerInvariant() switch
    {
        "responses" or "response" => ApiMode.Responses,
        "images" or "image" or "generations" => ApiMode.Images,
        "edit" or "edits" => ApiMode.Edit,
        _ => throw new CliException("--mode 只支持 responses、images 或 edit。")
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
