using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GptImageCli;

internal static class ConfigRegressionTests
{
    private const string ConfigKey = "synthetic-proxy-config-key";
    private const string OtherKey = "synthetic-other-node-key";
    private const string ExternalKey = "synthetic-official-environment-key";
    private const string ExplicitKey = "synthetic-explicit-override-key";
    private const string Origin = "https://offline.invalid";
    private static readonly string[] KeyVariables = ["GPT_IMAGE_API_KEY", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY"];
    private static readonly string[] UrlVariables = ["GPT_IMAGE_ENDPOINT", "OPENAI_BASE_URL", "AZURE_OPENAI_ENDPOINT"];

    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "config-regressions")).FullName;
        var configPath = Path.Combine(directory, "config.json");
        var serial = 0;
        JsonObject Node(string url, int type = 0, int route = 0, string key = ConfigKey, string id = "proxy") => new()
        {
            ["Id"] = id, ["Name"] = id, ["IsEnabled"] = true, ["BaseUrl"] = url, ["ApiKey"] = key,
            ["EndpointType"] = type, ["AuthMode"] = 0, ["ApiKeyHeaderMode"] = 0, ["ImageApiRouteMode"] = route,
            ["Models"] = new JsonArray(new JsonObject { ["ModelId"] = "gpt-image-2", ["Capabilities"] = 2 })
        };
        Task Write(JsonObject node, bool defaultReference = false, JsonObject? other = null)
        {
            var nodes = new JsonArray(node);
            if (other is not null) nodes.Add(other);
            var config = new JsonObject { ["Endpoints"] = nodes };
            if (defaultReference) config["MediaGenConfig"] = new JsonObject
            { ["ImageModelRef"] = new JsonObject { ["EndpointId"] = "proxy", ["ModelId"] = "gpt-image-2" } };
            return File.WriteAllTextAsync(configPath, config.ToJsonString());
        }
        string[] Args(string mode, params string[] extra) => ["--config", configPath, "--prompt", "offline",
            "--mode", mode, .. mode == "edit" ? new[] { "--image", source } : [], .. extra];
        async Task Reject(string mode, string? expectedMessage, params string[] extra)
        {
            using var handler = new Handler(_ => throw new Exception("Unexpected HTTP"));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var args = Args(mode, ["--json", .. extra]);
            if (extra.Contains("--no-config")) args = args[2..];
            var exit = await CliApplication.RunAsync(args, output, error, handler);
            using var report = JsonDocument.Parse(output.ToString());
            check(exit == 2 && report.RootElement.GetProperty("exit_code").GetInt32() == 2 &&
                report.RootElement.GetProperty("http_status").ValueKind == JsonValueKind.Null, "regression reject exit 2 before HTTP");
            check(handler.Calls == 0 && !error.ToString().Contains("POST "), "regression zero POST, no silent config-key fallback");
            check(expectedMessage is null || error.ToString().Contains(expectedMessage), "rejection identifies missing explicit target/source");
            CheckSecrets(output.ToString() + error);
        }
        void CheckSecrets(string text)
        {
            check(new[] { ConfigKey, OtherKey, ExternalKey, ExplicitKey }.All(k => !text.Contains(k) && !text.Contains(Uri.EscapeDataString(k))),
                "stdout/stderr isolate all synthetic keys");
        }
        async Task Wire(string mode, string expectedPath, string expectedKey, int type = 0, params string[] extra)
        {
            var configured = (await CommandLineParser.ParseAsync(Args(mode, extra))).ConfiguredRequestUrl is not null;
            using var handler = new Handler(request =>
            {
                check(request.Method == HttpMethod.Post && request.RequestUri!.GetLeftPart(UriPartial.Authority) == Origin &&
                    request.RequestUri.PathAndQuery == expectedPath, "matrix exact authority/path/query preserves proxy prefix");
                check(type == 1 ? request.Headers.GetValues("api-key").Single() == expectedKey && request.Headers.Authorization is null
                    : request.Headers.Authorization?.Parameter == expectedKey && !request.Headers.Contains("api-key"), "only intended key in exact auth header");
                var headers = request.Headers.ToString();
                check(new[] { ConfigKey, OtherKey, ExternalKey, ExplicitKey }.Where(k => k != expectedKey).All(k => !headers.Contains(k)),
                    "unselected/overridden keys absent from request headers");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } })) });
            });
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await CliApplication.RunAsync(Args(mode, ["--json", "--output", Path.Combine(directory, $"wire-{++serial}.png"), .. extra]), output, error, handler);
            check(exit == 0 && handler.Calls == 1, "matrix successful request without retry");
            CheckSecrets(output.ToString() + error);
            check(configured ? !error.ToString().Contains(Origin) : error.ToString().Contains(Origin), "only configured POST hides URL; standalone behavior unchanged");
        }

        // Explicit and all supported environment keys must not inherit an automatic target,
        // even with one eligible node or a valid global default pointing at a proxy.
        try
        {
            foreach (var variable in KeyVariables.Append("explicit"))
            foreach (var mode in new[] { "images", "edit", "responses" })
            foreach (var useDefault in new[] { false, true })
            {
                await Write(Node(Origin + "/Proxy"), useDefault, useDefault ? Node(Origin + "/Other", key: OtherKey, id: "other") : null);
                string[] keyArgs = variable == "explicit" ? ["--api-key", ExplicitKey] : [];
                if (variable != "explicit") Environment.SetEnvironmentVariable(variable, ExternalKey);
                var message = variable == "explicit" ? "显式 --api-key" : "环境密钥";
                await Reject(mode, message, keyArgs);
                // Missing/unreadable config must not mask the missing-target guard.
                await Reject(mode, "明确目标", [.. keyArgs, "--config", Path.Combine(directory, "missing.json")]);
                await Reject(mode, "明确目标", [.. keyArgs, "--no-config"]);
                foreach (var selector in new[] { "--endpoint-id", "--endpoint-name" })
                {
                    var action = mode == "responses" ? "responses" : mode == "edit" ? "images/edits" : "images/generations";
                    await Wire(mode, "/Proxy/v1/" + action, variable == "explicit" ? ExplicitKey : ExternalKey, 0,
                        [.. keyArgs, selector, "proxy"]);
                    if (variable != "explicit")
                        await Wire(mode, "/Proxy/v1/" + action, ExplicitKey, 0, [selector, "proxy", "--api-key", ExplicitKey]);
                }
                if (variable != "explicit") Environment.SetEnvironmentVariable(variable, null);
            }
            await Write(Node(Origin + "/Proxy"));
            foreach (var variable in UrlVariables)
            {
                Environment.SetEnvironmentVariable(variable, Origin + "/Chosen");
                Environment.SetEnvironmentVariable("OPENAI_API_KEY", ExternalKey);
                await Wire("images", "/Chosen/v1/images/generations", ExternalKey);
                Environment.SetEnvironmentVariable(variable, null);
            }
            await Wire("images", "/Explicit/v1/images/generations", ExternalKey, 0, "--endpoint", Origin + "/Explicit");
        }
        finally
        {
            foreach (var variable in KeyVariables.Concat(UrlVariables)) Environment.SetEnvironmentVariable(variable, null);
        }

        // Expected tails are independent fixtures, not computed with production URL helpers.
        (string Tail, string OpenAi, string Azure)[] tails = [
            ("", "/v1", "/openai/v1"), ("/v1", "/v1", "/v1"), ("/openai", "/openai/v1", "/openai/v1"),
            ("/openai/v1", "/openai/v1", "/openai/v1"), ("/V1", "/V1", "/V1"),
            ("/OPENAI/V1", "/OPENAI/V1", "/OPENAI/V1"), ("/v10", "/v10/v1", "/v10/openai/v1"),
            ("/openai-v1", "/openai-v1/v1", "/openai-v1/openai/v1"), ("/v1/tenant", "/v1/tenant/v1", "/v1/tenant/openai/v1")];
        for (var type = 0; type <= 2; type++)
        foreach (var mode in new[] { "images", "edit", "responses" })
        foreach (var route in type == 2 ? new[] { 0, 1, 2 } : new[] { 0, 1 })
        foreach (var prefix in new[] { "", "/Proxy/v1/Tenant%20A" })
        foreach (var tail in tails)
        foreach (var slash in new[] { "", "/" })
        {
            var baseUrl = Origin + prefix + tail.Tail;
            await Write(Node(baseUrl + slash, type, route));
            var action = mode == "responses" ? "responses" : mode == "edit" ? "images/edits" : "images/generations";
            var raw = type == 2 && (mode == "responses" || route == 2);
            var expected = prefix + (raw ? tail.Tail : type == 1 ? tail.Azure : tail.OpenAi) + "/" + action;
            if (type == 2 && mode == "responses") expected += "?api-version=2025-04-01-preview";
            else if (raw && mode == "images") expected += "?api-version=2025-03-01-preview";
            var parsed = await CommandLineParser.ParseAsync(Args(mode, "--endpoint", baseUrl));
            check(parsed.Endpoint == baseUrl && parsed.ApiKey == ConfigKey, "request normalization never rewrites matching BaseUrl");
            await Wire(mode, expected, ConfigKey, type, "--endpoint", baseUrl);
        }

        // Similar versioned URLs must never become equivalent for borrowing credentials.
        foreach (var tail in new[] { "", "/v1", "/openai/v1" })
        foreach (var otherTail in new[] { "", "/v1", "/openai/v1" }.Where(t => t != tail))
        foreach (var mode in new[] { "images", "edit", "responses" })
        {
            await Write(Node(Origin + "/Proxy" + tail));
            await Reject(mode, "未唯一匹配", "--endpoint", Origin + "/Proxy" + otherTail);
            await Reject(mode, "不一致", "--endpoint", Origin + "/Proxy" + otherTail, "--endpoint-id", "proxy");
            await Write(Node(Origin + "/Proxy" + tail), other: Node(Origin + "/Proxy" + otherTail, key: OtherKey, id: "other"));
            var parsed = await CommandLineParser.ParseAsync(Args(mode, "--endpoint", Origin + "/Proxy" + otherTail));
            check(parsed.ApiKey == OtherKey, "distinct versioned BaseUrl selects only its own credential");
        }

        // Query rejection alone is insufficient: active key can occur in a path, version or exception.
        foreach (var json in new[] { false, true })
        foreach (var outcome in new[] { "success", "http400", "network" })
        {
            var secret = "synthetic-config+secret/value";
            var encoded = Uri.EscapeDataString(secret);
            var node = Node(Origin + "/Proxy/" + encoded, 2, key: secret);
            node["ApiVersion"] = secret;
            await Write(node);
            using var handler = new Handler(request =>
            {
                check(request.Headers.Authorization?.Parameter == secret && request.RequestUri!.Query.Contains(encoded), "secret fixture reaches header/query only on fake transport");
                if (outcome == "network") throw new HttpRequestException("synthetic failure " + request.RequestUri + " " + secret);
                return Task.FromResult(new HttpResponseMessage(outcome == "success" ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                { Content = new StringContent(outcome == "success" ? JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } }) : "{\"error\":{\"message\":\"" + secret + "\"}}") });
            });
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await CliApplication.RunAsync(Args("responses", ["--output", Path.Combine(directory, $"secret-{++serial}.png"), .. json ? new[] { "--json" } : []]), output, error, handler);
            check(exit == (outcome == "success" ? 0 : 1) && handler.Calls == 1, "secret logging scenario expected exit and single POST");
            var text = output.ToString() + error;
            check(!text.Contains(secret) && !text.Contains(encoded) && !text.Contains(Origin), "config key raw/encoded URL and network details absent from both streams");
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return respond(request); }
    }
}