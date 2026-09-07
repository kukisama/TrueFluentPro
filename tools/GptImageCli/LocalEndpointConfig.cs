using System.Text.Json;

namespace GptImageCli;

internal sealed record EndpointSummary(string id, string name, string[] models);
internal sealed record ConfigConnection(string Endpoint, string Key, string Model, string LogicalModel, AuthMode Auth, string RequestUrl);

// Deliberately independent of ConfigurationService: no migrations, backups, writes or login.
internal static class LocalEndpointConfig
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private static JsonDocument Read(string? path)
    {
        try
        {
            path ??= Path.Combine(Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? appData
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrueFluentPro", "config.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new CliException("配置超过 4 MiB 限制；请使用精简的 --config 文件。");
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new CliException("配置读取期间发生变化；未发送请求。");
            var document = JsonDocument.Parse(bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0));
            try { ValidateObject(document.RootElement); return document; }
            catch { document.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new CliException("无法只读加载配置（文件缺失、不可读或 JSON 无效）；请检查 --config，或用 --no-config 并显式提供连接。"); }
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new CliException("配置结构无效；未发送请求。");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new CliException("配置包含重复字段；未发送请求。");
            if (property.Value.ValueKind == JsonValueKind.Object) ValidateObject(property.Value);
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) ValidateObject(item);
        }
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default
        : value.ValueKind != JsonValueKind.Object ? throw new CliException("配置对象结构无效；未发送请求。") : value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name)
    {
        var field = Field(value, name);
        return field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : field.ValueKind == JsonValueKind.String
            ? field.GetString()!.Trim() : throw new CliException("配置字符串字段类型无效；未发送请求。");
    }
    private static int Number(JsonElement value, string name)
    {
        var field = Field(value, name);
        return field.ValueKind == JsonValueKind.Undefined ? 0 : field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out var number) ? number : throw new CliException("配置枚举字段无效；未发送请求。");
    }
    private static JsonElement[] Array(JsonElement value, string name)
    {
        var field = Field(value, name);
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [];
        if (field.ValueKind != JsonValueKind.Array || field.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Object))
            throw new CliException("配置列表结构无效；未发送请求。");
        return field.EnumerateArray().ToArray();
    }
    private static JsonElement[] Models(JsonElement endpoint) => Array(endpoint, "Models").Where(m => (Number(m, "Capabilities") & 2) != 0 && Text(m, "ModelId") != "").ToArray();
    private static bool Enabled(JsonElement endpoint)
    {
        var flag = Field(endpoint, "IsEnabled");
        if (flag.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)) throw new CliException("配置 IsEnabled 无效。");
        return flag.ValueKind != JsonValueKind.False;
    }
    private static bool Eligible(JsonElement endpoint) => Enabled(endpoint) && Number(endpoint, "EndpointType") is >= 0 and <= 2 && Models(endpoint).Length > 0;
    private static JsonElement[] Endpoints(JsonElement root)
    {
        var endpoints = Array(root, "Endpoints");
        if (endpoints.Any(e => Text(e, "Id") == "") || endpoints.GroupBy(e => Text(e, "Id"), StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new CliException("配置节点 ID 缺失或重复；请修正配置。");
        return endpoints;
    }
    private static JsonElement Unique(JsonElement[] values, string message) => values.Length == 1 ? values[0] : throw new CliException(message);
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new CliException("连接 URL 无效；--endpoint 必须是 HTTP/HTTPS URL，而非节点名称。");
        return uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/') + uri.Query;
    }

    public static EndpointSummary[] List(string? path)
    {
        using var document = Read(path);
        var endpoints = Endpoints(document.RootElement);
        var secrets = endpoints.SelectMany(e => new[] { Text(e, "ApiKey"), Text(e, "BaseUrl") }
            .Concat(Array(e, "Models").Where(m => Text(m, "DeploymentName") != Text(m, "ModelId")).Select(m => Text(m, "DeploymentName")))).Where(s => s.Length > 0).ToArray();
        string Safe(string value) => value.Contains("://") || value.Any(char.IsControl) || secrets.Any(s => value.Contains(s, StringComparison.Ordinal)) ? "[已隐藏]" : value;
        return endpoints.Where(Eligible).Select(e => new EndpointSummary(Safe(Text(e, "Id")), Safe(Text(e, "Name")), Models(e).Select(m => Safe(Text(m, "ModelId"))).ToArray())).ToArray();
    }

    public static ConfigConnection Resolve(string? path, string? name, string? id, string? endpoint, string? key, string? model, ApiMode mode)
    {
        try { return ResolveCore(path, name, id, endpoint, key, model, mode); }
        catch (InvalidOperationException) { throw new CliException("配置字段类型无效；未发送请求。"); }
    }

    private static ConfigConnection ResolveCore(string? path, string? name, string? id, string? endpoint, string? key, string? model, ApiMode mode)
    {
        using var document = Read(path);
        var root = document.RootElement;
        var endpoints = Endpoints(root);
        var reference = Field(Field(root, "MediaGenConfig"), "ImageModelRef");
        var referenceId = Text(reference, "EndpointId");
        var referenceModel = Text(reference, "ModelId");
        JsonElement selected;
        if (name is not null || id is not null)
            selected = Unique(endpoints.Where(e => id is not null ? Text(e, "Id") == id : Same(Text(e, "Name"), name!)).ToArray(), "节点未找到或名称重复；请用 --list-endpoints / --endpoint-id 选择。");
        else if (endpoint is not null)
            selected = Unique(endpoints.Where(e => Eligible(e) && NormalizeUrl(Text(e, "BaseUrl")) == NormalizeUrl(endpoint)).ToArray(), "URL 未唯一匹配配置 BaseUrl；完整 API URL 不等于 BaseUrl。请显式提供 --api-key，不能按 host 借用密钥。");
        else if (reference.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            selected = Unique(endpoints.Where(e => Text(e, "Id") == referenceId && Eligible(e) && Models(e).Count(m => Text(m, "ModelId") == referenceModel) == 1).ToArray(), "主程序默认 ImageModelRef 已失效；请修正或用 --endpoint-name / --endpoint-id 显式选择。");
        }
        else selected = Unique(endpoints.Where(Eligible).ToArray(), "没有唯一启用的图片节点；请用 --list-endpoints 后指定 --endpoint-name / --endpoint-id。");
        if (!Eligible(selected)) throw new CliException("所选节点已禁用、不含图片模型或 EndpointType 不支持（仅 0/1/2）。");
        var baseUrl = NormalizeUrl(Text(selected, "BaseUrl"));
        if (endpoint is not null && NormalizeUrl(endpoint) != baseUrl) throw new CliException("--endpoint URL 与所选节点 BaseUrl 不一致；拒绝使用配置密钥。");
        if (new Uri(baseUrl).Query.Length > 0) throw new CliException("配置 BaseUrl 不支持查询参数；请用 --no-config 显式提供完整 URL、key 和 --auth。");
        if (Number(selected, "AuthMode") != 0) throw new CliException("不支持配置 AAD 或未知认证模式；不会登录或使用其 ApiKey。请用 --no-config、显式 URL/token 和 --auth bearer 独立运行。");
        var type = Number(selected, "EndpointType");
        var profile = Text(selected, "ProfileId");
        string[] builtins = ["builtin.openai.compatible", "builtin.microsoft.azure-openai", "builtin.microsoft.apim-gateway"];
        if (profile != "" && !Same(profile, builtins[type])) throw new CliException("自定义或不匹配的 ProfileId 不受支持；请用 --no-config 显式提供 URL、key、--auth 和模型。");
        var header = Number(selected, "ApiKeyHeaderMode");
        if (header is < 0 or > 2) throw new CliException("配置 ApiKeyHeaderMode 不支持。");
        var auth = header == 1 || (header == 0 && type == 1) ? AuthMode.ApiKey : AuthMode.Bearer;
        var models = Models(selected);
        JsonElement chosen = default;
        if (model is not null)
        {
            var matches = models.Where(m => Same(Text(m, "ModelId"), model) || Same(Text(m, "DeploymentName"), model)).ToArray();
            if (matches.Length > 1) throw new CliException("图片模型或部署名重复；请修正配置。");
            if (matches.Length == 1) chosen = matches[0];
        }
        else
        {
            var preferred = Text(selected, "Id") == referenceId ? models.Where(m => Text(m, "ModelId") == referenceModel).ToArray() : [];
            if (preferred.Length == 1) chosen = preferred[0];
            else
            {
                var image2 = models.Where(m => Same(Text(m, "ModelId"), "gpt-image-2")).ToArray();
                chosen = Unique(image2.Length > 0 ? image2 : models, "图片模型不唯一；请指定 --image-model。");
            }
        }
        var logical = chosen.ValueKind == JsonValueKind.Undefined ? model! : Text(chosen, "ModelId");
        var deployment = Text(chosen, "DeploymentName");
        var requestModel = deployment == "" ? logical : deployment;
        key ??= Text(selected, "ApiKey");
        if (string.IsNullOrWhiteSpace(key)) throw new CliException("所选节点缺少 ApiKey；请提供 --api-key 或环境密钥。");
        if (key.Any(char.IsControl)) throw new CliException("认证密钥包含非法控制字符；未发送请求。");
        var route = Number(selected, "ImageApiRouteMode");
        if (route is < 0 or > 2 || (route == 2 && type != 2 && mode != ApiMode.Responses)) throw new CliException("内建 profile 未声明此图片路由；请用 --no-config 显式提供完整 URL。");
        var action = mode == ApiMode.Edit ? "edits" : "generations";
        var suffix = mode == ApiMode.Responses ? (type == 2 ? "/responses" : type == 1 ? "/openai/v1/responses" : "/v1/responses")
            : (type == 1 ? "/openai/v1/images/" : route == 2 ? "/images/" : "/v1/images/") + action;
        var version = Text(selected, "ApiVersion");
        // Match the first built-in candidate only; never retry/fall back on the network.
        if (type == 2 && mode == ApiMode.Responses && (version == "" || version == "2025-03-01-preview")) version = "2025-04-01-preview";
        if (type == 2 && route == 2 && mode == ApiMode.Images && version == "") version = "2025-03-01-preview";
        var query = type == 2 && (mode == ApiMode.Responses || (route == 2 && mode == ApiMode.Images)) ? "?api-version=" + Uri.EscapeDataString(version) : "";
        return new(baseUrl, key, requestModel, logical, auth, RequestFactory.JoinConfiguredPath(baseUrl, suffix) + query);
    }
}