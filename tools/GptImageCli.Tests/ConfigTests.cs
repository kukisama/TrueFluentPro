using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GptImageCli;

internal static class ConfigTests
{
    private const string Key = "synthetic-config-secret";
    private const string Url = "https://offline.invalid/Proxy";
    private static JsonObject Model(string id = "gpt-image-2", string deployment = "private-deployment", int capability = 2) => new()
        { ["ModelId"] = id, ["DeploymentName"] = deployment, ["Capabilities"] = capability };
    private static JsonObject Endpoint(string id = "one", string name = "友好 Node", int type = 0) => new()
    {
        ["Id"] = id, ["Name"] = name, ["IsEnabled"] = true, ["EndpointType"] = type,
        ["BaseUrl"] = Url, ["ApiKey"] = Key, ["AuthMode"] = 0, ["ApiKeyHeaderMode"] = 0,
        ["ImageApiRouteMode"] = 0, ["Models"] = new JsonArray(Model())
    };
    private static JsonObject Config(params JsonObject[] endpoints) => new()
    {
        ["Endpoints"] = new JsonArray(endpoints.Select(e => e.DeepClone()).ToArray()),
        ["MediaGenConfig"] = new JsonObject { ["ImageSize"] = "16x16", ["ImageQuality"] = "high", ["ImageCount"] = 9, ["ImageFormat"] = "webp" }
    };
    private static void Reference(JsonObject config, string id, string model) => config["MediaGenConfig"]!["ImageModelRef"] =
        new JsonObject { ["EndpointId"] = id, ["ModelId"] = model };

    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "config-sandbox")).FullName;
        var configPath = Path.Combine(directory, "config.json");
        var serial = 0;
        string[] Args(params string[] extra) => ["--config", configPath, "--prompt", "offline", .. extra];
        Task Write(JsonObject config) => File.WriteAllTextAsync(configPath, config.ToJsonString());
        Task<CliOptions> Parse(params string[] extra) => CommandLineParser.ParseAsync(Args(extra));

        async Task<(int Exit, JsonElement Report, string Error, string Output)> Run(params string[] extra)
        {
            using var handler = new Handler(_ => throw new Exception("Unexpected POST in config rejection/list test"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var args = extra.Contains("--no-config") ? extra : new[] { "--config", configPath }.Concat(extra).ToArray();
            var exit = await CliApplication.RunAsync(["--json", .. args], output, error, handler);
            using var json = JsonDocument.Parse(output.ToString());
            check(handler.Calls == 0 && !error.ToString().Contains("POST "), "config offline command has zero HTTP");
            check(!(output.ToString() + error).Contains(Key) && !(output.ToString() + error).Contains("private-deployment") &&
                !(output.ToString() + error).Contains(Url), "config failure/list streams hide connection and deployment secrets");
            return (exit, json.RootElement.Clone(), error.ToString(), output.ToString());
        }
        async Task Reject(string label, params string[] extra)
        {
            var run = await Run(["--prompt", "offline", .. extra]);
            check(run.Exit == 2 && run.Report.GetProperty("exit_code").GetInt32() == 2 && !run.Report.GetProperty("ok").GetBoolean() &&
                run.Report.GetProperty("http_status").ValueKind == JsonValueKind.Null, label);
        }
        async Task Wire(string label, string expectedPath, string expectedModel, AuthMode auth, params string[] extra)
        {
            using var handler = new Handler(async request =>
            {
                check(request.RequestUri!.PathAndQuery == expectedPath && request.Method == HttpMethod.Post, label + " exact route");
                check(auth == AuthMode.Bearer ? request.Headers.Authorization?.Parameter == Key && !request.Headers.Contains("api-key") :
                    request.Headers.GetValues("api-key").Single() == Key && request.Headers.Authorization is null, label + " exact auth header");
                if (request.Content is MultipartFormDataContent multipart)
                {
                    var parts = multipart.ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                    check(await parts["model"].ReadAsStringAsync() == expectedModel, label + " deployment multipart");
                }
                else
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    check(request.Headers.Contains("x-ms-oai-image-generation-deployment") ?
                        request.Headers.GetValues("x-ms-oai-image-generation-deployment").Single() == expectedModel :
                        body.RootElement.GetProperty("model").GetString() == expectedModel, label + " deployment JSON/header");
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } })) };
            });
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await CliApplication.RunAsync(Args(["--json", "--output", Path.Combine(directory, $"wire-{++serial}.png"), .. extra]), output, error, handler);
            using var report = JsonDocument.Parse(output.ToString());
            check(exit == 0 && handler.Calls == 1 && report.RootElement.GetProperty("files").GetArrayLength() == 1, label + " success with no retry");
        }

        await Write(Config(Endpoint()));
        var before = await File.ReadAllBytesAsync(configPath);
        var stamp = File.GetLastWriteTimeUtc(configPath);
        var one = await Parse();
        check(one.Endpoint == Url && one.ApiKey == Key && one.ImageModel == "private-deployment" && one.LogicalImageModel == "gpt-image-2", "unique config connection and separate logical/deployment models");
        check(one.Size == "1024x640" && one.Quality == "medium" && one.Count == 1 && one.OutputFormat == "png" && one.Mode == ApiMode.Images, "CLI generation defaults never imported");
        check((await Parse("--endpoint-name", "友好 nODE")).ImageModel == "private-deployment", "friendly name exact case-insensitive");
        await Reject("name is not substring", "--endpoint-name", "友好");
        await Reject("unknown id", "--endpoint-id", "missing");
        await Reject("id is case-sensitive", "--endpoint-id", "ONE");
        await Reject("selectors mutually exclusive", "--endpoint-name", "友好 Node", "--endpoint-id", "one");
        await Reject("URL remains URL not name", "--endpoint", "友好 Node");
        foreach (var badUrl in new[] { "https://other.invalid/Proxy", "http://offline.invalid/Proxy", "https://offline.invalid:444/Proxy",
            "https://offline.invalid/proxy", "https://offline.invalid/Other", Url + "/v1/images/generations", Url + "?x=1",
            "https://offline.invalid.evil/Proxy", "https://user:pass@offline.invalid/Proxy", Url + "#fragment", "ftp://offline.invalid/Proxy" })
        {
            await Reject("URL boundary without selector: " + badUrl, "--endpoint", badUrl);
            var selected = await Parse("--endpoint", badUrl, "--endpoint-id", "one");
            check(selected.Endpoint == Url && selected.ApiKey == Key, "selector ignores external URL: " + badUrl);
        }
        check((await Parse("--endpoint", "HTTPS://OFFLINE.INVALID:443/Proxy/")).ApiKey == Key, "same normalized scheme/host/default port/trailing slash accepted");
        var selectedConnection = await Parse("--endpoint", "https://other.invalid", "--api-key", "explicit", "--endpoint-id", "one");
        check(selectedConnection.Endpoint == Url && selectedConnection.ApiKey == Key && selectedConnection.ApiKeySource == "节点配置", "selector overrides explicit address/key as a pair");
        await Reject("deployment retains image2 size validation", "--size", "16x16");
        await Reject("explicit deployment retains logical image2 size validation", "--image-model", "private-deployment", "--size", "16x16");
        await Reject("edit deployment retains logical image2 size validation", "--mode", "edit", "--image", source, "--size", "16x16");
        check((await Parse("--image-model", "caller-model")).ImageModel == "caller-model", "explicit unlisted model keeps standalone override semantics");
        await Wire("friendly config", "/Proxy/v1/images/generations", "private-deployment", AuthMode.Bearer, "--endpoint-name", "友好 Node");
        var after = await File.ReadAllBytesAsync(configPath);
        check(before.SequenceEqual(after) && stamp == File.GetLastWriteTimeUtc(configPath), "config content and write timestamp untouched");

        var first = Endpoint();
        first["Models"] = new JsonArray(Model(), Model("gpt-image-1", "default-private"));
        var config = Config(first, Endpoint("two", "第二节点"));
        Reference(config, "one", "gpt-image-1");
        await Write(config);
        check((await Parse()).ImageModel == "default-private", "valid global default chooses node and model before image2");
        check((await Parse("--image-model", "gpt-image-2")).LogicalImageModel == "gpt-image-2", "explicit model overrides default reference");
        check((await Parse("--endpoint-id", "two")).LogicalImageModel == "gpt-image-2", "reference on another node ignored for explicit selection");
        Reference(config, "missing", "gpt-image-1");
        await Write(config);
        await Reject("invalid default endpoint fails instead of selecting another");
        check((await Parse("--endpoint-id", "two")).ApiKey == Key, "selector can bypass stale default");
        Reference(config, "one", "missing-model");
        await Write(config);
        await Reject("invalid default model fails instead of image2 fallback");
        ((JsonArray)config["Endpoints"]!)[0]!["IsEnabled"] = false;
        Reference(config, "one", "gpt-image-1");
        await Write(config);
        await Reject("disabled default does not silently choose unique other endpoint");
        await Write(Config(Endpoint(), Endpoint("two", "第二节点")));
        await Reject("multiple nodes require selector");
        await Reject("same URL multiple keys is ambiguous", "--endpoint", Url);
        await Write(Config(Endpoint(), Endpoint("two", "友好 node")));
        await Reject("duplicate friendly names rejected", "--endpoint-name", "友好 NODE");
        check((await Parse("--endpoint-id", "two")).ApiKey == Key, "unique ID disambiguates duplicate names");
        await Write(Config(Endpoint(), Endpoint("one", "different name")));
        await Reject("duplicate IDs rejected", "--endpoint-name", "different name");
        await Reject("duplicate IDs also rejected for list", "--list-endpoints");

        foreach (var (property, value) in new (string, JsonNode?)[] { ("IsEnabled", JsonValue.Create(false)), ("EndpointType", JsonValue.Create(3)),
            ("EndpointType", JsonValue.Create(-1)), ("AuthMode", JsonValue.Create(1)), ("AuthMode", JsonValue.Create(9)),
            ("ApiKey", JsonValue.Create(" ")), ("ApiKey", JsonValue.Create("abc\nsecret")), ("ApiKeyHeaderMode", JsonValue.Create(7)),
            ("Models", new JsonArray(Model(capability: 1))), ("Models", new JsonArray()), ("ProfileId", JsonValue.Create("custom.secret.profile")),
            ("ProfileId", JsonValue.Create("builtin.microsoft.azure-openai")), ("ImageApiRouteMode", JsonValue.Create(99)),
            ("IsEnabled", JsonValue.Create("false")), ("EndpointType", JsonValue.Create("AzureOpenAi")), ("AuthMode", null),
            ("BaseUrl", JsonValue.Create("not-a-url")), ("BaseUrl", JsonValue.Create(Url + "?secret=true")), ("Id", JsonValue.Create("")) })
        {
            var endpoint = Endpoint(); endpoint[property] = value;
            await Write(Config(endpoint));
            await Reject("invalid/unsupported config field " + property, "--endpoint-name", "友好 Node");
        }
        var aad = Endpoint(); aad["AuthMode"] = 1;
        await Write(Config(aad));
        var unsupported = await Run("--prompt", "offline");
        check(unsupported.Error.Contains("AAD") && unsupported.Error.Contains("--no-config"), "AAD error explains safe independent bearer alternative without login");
        var independent = await Parse("--endpoint", Url, "--api-key", "own-token", "--auth", "bearer");
        check(independent.AuthMode == AuthMode.Bearer && independent.ApiKey == "own-token" && independent.ConfiguredRequestUrl is null, "complete independent bearer does not inspect AAD config");

        var many = Endpoint();
        many["Models"] = new JsonArray(Model("other-1", ""), Model("other-2", ""));
        await Write(Config(many));
        await Reject("multiple non-image2 models need explicit model");
        check((await Parse("--image-model", "other-2")).ImageModel == "other-2", "explicit image model resolves ambiguity");
        many["Models"] = new JsonArray(Model("gpt-image-2", "one-private"), Model("gpt-image-2", "two-private"));
        await Write(Config(many));
        await Reject("duplicate image2 rejected");
        await Reject("explicit duplicate model rejected", "--image-model", "gpt-image-2");
        many["Models"] = new JsonArray(Model("only-model", "", 3));
        await Write(Config(many));
        check((await Parse()).ImageModel == "only-model", "single image-capable model supports bit flags and blank deployment");

        var savedVariables = new[] { "GPT_IMAGE_ENDPOINT", "GPT_IMAGE_API_KEY", "GPT_IMAGE_MODEL", "AZURE_OPENAI_API_VERSION", "OPENAI_BASE_URL", "OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_API_KEY" };
        try
        {
            await Write(Config(Endpoint()));
            Environment.SetEnvironmentVariable("GPT_IMAGE_MODEL", "environment-model");
            Environment.SetEnvironmentVariable("GPT_IMAGE_API_KEY", "environment-key");
            await Reject("environment key without target cannot select configuration automatically");
            check((await Parse("--endpoint-id", "one")).ImageModel == "environment-model" && (await Parse("--endpoint-id", "one")).ApiKey == Key, "selector overrides environment key while model stays independent");
            check((await Parse("--endpoint-id", "one", "--image-model", "gpt-image-2", "--api-key", "explicit-key")).ApiKey == Key &&
                (await Parse("--endpoint-id", "one", "--image-model", "gpt-image-2")).LogicalImageModel == "gpt-image-2", "selector overrides explicit key while explicit model remains effective");
            Environment.SetEnvironmentVariable("GPT_IMAGE_ENDPOINT", "https://environment.invalid");
            check((await Parse()).Endpoint == "https://environment.invalid" && (await Parse()).ConfiguredRequestUrl is null, "complete environment independent of conflicting config");
            check((await Parse("--endpoint-id", "one")).Endpoint == Url, "selector overrides conflicting environment URL");
            check(Environment.GetEnvironmentVariable("GPT_IMAGE_ENDPOINT") == "https://environment.invalid" &&
                Environment.GetEnvironmentVariable("GPT_IMAGE_API_KEY") == "environment-key", "selector leaves process environment unchanged");
            check((await Parse("--endpoint", Url, "--endpoint-id", "one")).Endpoint == Url, "explicit endpoint overrides environment with selector");
            Environment.SetEnvironmentVariable("GPT_IMAGE_API_KEY", null);
            check((await Parse("--endpoint-id", "one")).ApiKey == Key, "selector ignores environment URL even without external key");
            Environment.SetEnvironmentVariable("GPT_IMAGE_ENDPOINT", Url);
            check((await Parse()).ApiKey == Key, "environment URL gets key only from matched node");
            Environment.SetEnvironmentVariable("GPT_IMAGE_ENDPOINT", null);
            Environment.SetEnvironmentVariable("GPT_IMAGE_MODEL", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", "env-preview");
            using (var request = RequestFactory.Create(await Parse())) check(request.RequestUri!.Query == "?api-version=env-preview", "environment API version overrides configured route");
            using (var request = RequestFactory.Create(await Parse("--api-version", "explicit-preview"))) check(request.RequestUri!.Query == "?api-version=explicit-preview", "explicit API version precedes environment");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_VERSION", null);
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "https://openai-env.invalid");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "openai-env-key");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://azure-env.invalid");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "azure-env-key");
            check((await Parse()).Endpoint == "https://openai-env.invalid" && (await Parse()).ApiKey == "openai-env-key", "legacy environment priority preserved");
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            check((await Parse()).Endpoint == "https://azure-env.invalid" && (await Parse()).ApiKey == "azure-env-key", "legacy Azure environment standalone preserved");
        }
        finally { foreach (var variable in savedVariables) Environment.SetEnvironmentVariable(variable, null); }

        string[] profiles = ["builtin.openai.compatible", "builtin.microsoft.azure-openai", "builtin.microsoft.apim-gateway"];
        for (var type = 0; type <= 2; type++)
        for (var header = 0; header <= 2; header++)
        foreach (var mode in new[] { "images", "edit", "responses" })
        {
            var endpoint = Endpoint(type: type); endpoint["ProfileId"] = profiles[type]; endpoint["ApiKeyHeaderMode"] = header;
            endpoint["ApiVersion"] = "configured-preview";
            await Write(Config(endpoint));
            var path = type == 1 ? "/Proxy/openai/v1/" : "/Proxy/v1/";
            path += mode == "images" ? "images/generations" : mode == "edit" ? "images/edits" : "responses";
            if (type == 2 && mode == "responses") path = "/Proxy/responses?api-version=configured-preview";
            var expectedAuth = header == 1 || (header == 0 && type == 1) ? AuthMode.ApiKey : AuthMode.Bearer;
            string[] extra = mode == "edit" ? ["--image", source] : [];
            await Wire($"type={type} header={header} mode={mode}", path, "private-deployment", expectedAuth, ["--mode", mode, .. extra]);
            var parsed = await Parse("--auth", "bearer");
            check(parsed.AuthMode == AuthMode.Bearer && (await Parse("--auth", "api-key")).AuthMode == AuthMode.ApiKey, "explicit auth overrides configured header");
            check((await Parse("--auth", "auto")).AuthMode == expectedAuth, "explicit auto follows config rather than guessing from host");
        }
        foreach (var type in new[] { 0, 1, 2 })
        {
            var endpoint = Endpoint(type: type); endpoint["ImageApiRouteMode"] = 1;
            await Write(Config(endpoint));
            using (var request = RequestFactory.Create(await Parse())) check(request.RequestUri!.AbsolutePath == (type == 1 ? "/Proxy/openai/v1/images/generations" : "/Proxy/v1/images/generations"), "V1 route honors endpoint type independent of host");
            endpoint["ImageApiRouteMode"] = 2;
            await Write(Config(endpoint));
            if (type != 2) { await Reject("raw route absent in builtin profile rejected"); continue; }
            await Wire("APIM raw default version", "/Proxy/images/generations?api-version=2025-03-01-preview", "private-deployment", AuthMode.Bearer);
            await Wire("APIM raw edit no version template", "/Proxy/images/edits", "private-deployment", AuthMode.Bearer, "--mode", "edit", "--image", source);
            endpoint["ApiVersion"] = "config-preview";
            await Write(Config(endpoint));
            await Wire("APIM raw configured version", "/Proxy/images/generations?api-version=config-preview", "private-deployment", AuthMode.Bearer);
            await Wire("APIM explicit version replaces template version", "/Proxy/images/generations?api-version=cli-preview", "private-deployment", AuthMode.Bearer, "--api-version", "cli-preview");
        }
        foreach (var version in new[] { "", "2025-03-01-preview", "custom-preview" })
        {
            var endpoint = Endpoint(type: 2); endpoint["ApiVersion"] = version;
            await Write(Config(endpoint));
            await Wire("APIM Responses capability version " + version, "/Proxy/responses?api-version=" + (version == "custom-preview" ? version : "2025-04-01-preview"), "private-deployment", AuthMode.Bearer, "--mode", "responses");
        }

        var missingPath = Path.Combine(directory, "missing.json");
        foreach (var text in new[] { "{\"secret\":\"" + Key + "\", BROKEN", "", "null", "[]", "{\"Endpoints\":{}}", "{\"Endpoints\":[null]}",
            "{\"Endpoints\":[],\"Endpoints\":[]}", "{\"MediaGenConfig\":[],\"Endpoints\":[]}", "{\"MediaGenConfig\":{\"ImageModelRef\":\"bad\"},\"Endpoints\":[]}" })
        {
            await File.WriteAllTextAsync(configPath, text);
            await Reject("malformed JSON/schema fails safely");
            check((await Run("--help")).Exit == 0, "help bypasses malformed config");
            check((await Parse("--endpoint", Url, "--api-key", "own-key")).ConfiguredRequestUrl is null, "complete connection does not parse malformed config");
        }
        await Reject("missing config read failure", "--config", missingPath);
        check(!File.Exists(missingPath), "missing config not created");
        check((await Run("--help", "--config", missingPath)).Exit == 0, "help does not require existing config");
        await Reject("config path directory fails sanitized", "--config", directory);
        await Write(Config(Endpoint()));
        using (File.Open(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Reject("locked config rejected");
            check((await Parse("--endpoint", Url, "--api-key", "own-key")).ConfiguredRequestUrl is null, "complete connection never opens locked config");
            check((await Run("--help")).Exit == 0, "help never opens locked config");
        }
        await File.WriteAllTextAsync(configPath, new string(' ', 4 * 1024 * 1024 + 1));
        await Reject("config size limit");
        await File.WriteAllTextAsync(configPath, "\uFEFF" + Config(Endpoint()).ToJsonString());
        check((await Parse()).ApiKey == Key, "UTF8 BOM config supported");
        foreach (var extra in new[] { new[] { "--config", configPath }, new[] { "--endpoint-id", "one" }, new[] { "--endpoint-name", "友好 Node" }, new[] { "--list-endpoints" } })
            await Reject("no-config conflict", ["--no-config", .. extra]);
        await Reject("no-config missing connection never reads real config", "--no-config");
        check((await CommandLineParser.ParseAsync(["--no-config", "--endpoint", Url, "--api-key", "own-token", "--auth", "bearer", "--prompt", "offline"])).ApiKey == "own-token", "no-config complete independent connection");
        foreach (var extra in new[] { new[] { "--list-endpoints=true" }, new[] { "--no-config=true" }, new[] { "--endpoint-name=" },
            new[] { "--endpoint-id" }, new[] { "--config=" } }) await Reject("new parameter syntax", extra);

        var disabled = Endpoint("disabled", "disabled"); disabled["IsEnabled"] = false;
        var speech = Endpoint("speech", "speech"); speech["Models"] = new JsonArray(Model(capability: 8));
        var blankKey = Endpoint("empty", "无需密钥"); blankKey["ApiKey"] = "";
        var unsupportedType = Endpoint("unsupported", "unsupported", 3);
        var unsafeName = Endpoint("redact", "prefix-" + Key);
        // Give the AAD node an independent identity; listing must not authenticate it.
        aad["Id"] = "aad";
        await Write(Config(Endpoint(), aad, disabled, speech, blankKey, unsupportedType, unsafeName));
        var listing = await Run("--list-endpoints", "--prompt-file", missingPath);
        var entries = listing.Report.GetProperty("endpoints");
        check(listing.Exit == 0 && entries.GetArrayLength() == 4 && listing.Error.Contains("未列出节点") &&
            listing.Error.Contains("节点未启用") && listing.Error.Contains("图片能力模型") && listing.Error.Contains("节点类型不适用"),
            "list is offline with unchanged filtering and actionable exclusion diagnostics");
        check(entries[0].EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "id", "name", "models" }) &&
            entries[0].GetProperty("models")[0].GetString() == "gpt-image-2", "list allowlist contains only ID/name/logical models");
        check(entries[3].GetProperty("name").GetString() == "[已隐藏]", "list hides secrets even when embedded in friendly name");
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            check(await CliApplication.RunAsync(["--config", configPath, "--list-endpoints"], output, error) == 0 &&
                !output.ToString().Contains(Key) && !output.ToString().Contains(Url) && !output.ToString().Contains("private-deployment"), "human list excludes URL/key/deployment too");
        }
        await Write(Config());
        check((await Run("--list-endpoints")).Report.GetProperty("endpoints").GetArrayLength() == 0, "empty endpoint list succeeds");
        var sameDeployment = Endpoint(); sameDeployment["Models"] = new JsonArray(Model("gpt-image-2", "gpt-image-2"));
        await Write(Config(sameDeployment));
        check((await Run("--list-endpoints")).Report.GetProperty("endpoints")[0].GetProperty("models")[0].GetString() == "gpt-image-2", "logical model remains public when deployment has identical name");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return respond(request); }
    }
}