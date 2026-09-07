using System.Net;
using System.Text.Json;
using GptImageCli;
using CommandLineParser = OfflineCli;
using CliApplication = OfflineCli;

internal static class FeatureTests
{
    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        string[] Args(params string[] extra) =>
            ["--endpoint", "https://offline.invalid", "--api-key", "offline-secret-not-for-report", "--prompt", "offline prompt", "--mode", "responses", .. extra];

        async Task Reject(string name, params string[] extra)
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response("{}")));
            var run = await RunJson(Args(["--json", .. extra]), handler);
            check(run.Exit == 2 && handler.Calls == 0 && !run.Report.GetProperty("ok").GetBoolean() &&
                run.Report.GetProperty("exit_code").GetInt32() == 2 && run.Report.GetProperty("error").ValueKind == JsonValueKind.String &&
                run.Report.GetProperty("http_status").ValueKind == JsonValueKind.Null && run.Report.GetProperty("files").GetArrayLength() == 0,
                name + " exits 2 with single JSON before HTTP");
        }

        foreach (var option in "endpoint api-key prompt prompt-file mode image auth model image-model api-version size quality format output-format n count output out timeout-minutes mask background output-compression moderation user".Split(' '))
        {
            await Reject("missing " + option, "--" + option);
            await Reject("empty " + option, "--" + option + "=");
        }
        foreach (var extra in new[] {
            new[] { "--imaginary", "paid" }, new[] { "--stream" }, new[] { "--input-fidelity", "high" },
            new[] { "--input_fidelity=high" }, new[] { "--background", "--unknown" },
            new[] { "--help", "--invented" }, new[] { "--json=true" }, new[] { "--json", "false" },
            new[] { "--mode", "chat" }, new[] { "--n", "0" }, new[] { "--n", "11" },
            new[] { "--count", "-1" }, new[] { "--quality", "ultra" }, new[] { "--background", "clear" },
            new[] { "--moderation", "off" }, new[] { "--user", "someone" },
            new[] { "--format", "jpeg", "--background", "transparent" },
            new[] { "--output-compression", "50" }, new[] { "--format", "webp", "--output-compression", "-1" },
            new[] { "--format", "jpeg", "--output-compression", "101" }, new[] { "--format", "webp", "--output-compression", "1.5" },
            new[] { "--format", "webp", "--output-compression", "many" }, new[] { "--timeout-minutes", "35792" },
            new[] { "--timeout-minutes", "2147483647" }, new[] { "--mask", source },
            new[] { "--mode", "images", "--mask", source } })
            await Reject(string.Join(' ', extra), extra);

        foreach (var endpoint in new[] { "not-a-url", "/relative", "ftp://example.invalid", "file:///C:/temp", "https://", "https://user:pass@example.invalid", "https://example.invalid/#fragment" })
            await Reject("endpoint " + endpoint, "--endpoint", endpoint);
        foreach (var size in new[] { "1025x1024", "16x16", "3840x2176", "3856x1024", "3072x1008", "0x1024", "-1024x1024", "2147483647x2147483647", "1024x1024x1", "square" })
            await Reject("image2 size " + size, "--mode", "images", "--size", size);
        await Reject("edit size validation", "--mode", "edit", "--image", source, "--size", "16x16");

        var fakeMask = Path.Combine(root, "fake-mask.png");
        await File.WriteAllTextAsync(fakeMask, "not PNG");
        var jpgMask = Path.Combine(root, "mask.jpg");
        await File.WriteAllBytesAsync(jpgMask, png);
        foreach (var mask in new[] { Path.Combine(root, "missing.png"), root, fakeMask, jpgMask })
            await Reject("invalid mask " + Path.GetFileName(mask), "--mode", "edit", "--image", source, "--mask", mask);
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Reject("locked mask", "--mode", "edit", "--image", source, "--mask", source);

        foreach (var size in new[] { "auto", "1024x640", "640x1024", "3840x2160", "3072x1024", "1024x3072" })
            check((await CommandLineParser.ParseAsync(Args("--mode", "images", "--size", size))).Size == size, "accepted size boundary " + size);
        foreach (var extra in new[] { new[] { "--mode", "responses" }, new[] { "--mode", "images", "--image-model", "custom-deployment" } })
            check((await CommandLineParser.ParseAsync(Args([.. extra, "--size", "custom-size"]))).Size == "custom-size", "size restrictions limited to explicit image2 direct modes");
        foreach (var quality in new[] { "auto", "low", "medium", "high" })
            check((await CommandLineParser.ParseAsync(Args("--quality", quality))).Quality == quality, "quality " + quality);
        foreach (var background in new[] { "auto", "opaque", "transparent" })
            check((await CommandLineParser.ParseAsync(Args("--background", background))).Background == background, "background " + background);
        foreach (var moderation in new[] { "auto", "low" })
            check((await CommandLineParser.ParseAsync(Args("--moderation", moderation))).Moderation == moderation, "moderation " + moderation);
        foreach (var n in new[] { "1", "10" })
            check((await CommandLineParser.ParseAsync(Args("--n", n))).Count == int.Parse(n), "n boundary " + n);

        foreach (var mode in new[] { "images", "edit", "responses" })
        {
            string[] modeArgs = mode == "edit" ? ["--mode", mode, "--image", source] : ["--mode", mode];
            using var defaults = RequestFactory.Create(await CommandLineParser.ParseAsync(Args(modeArgs)));
            var defaultBody = await defaults.Content!.ReadAsStringAsync();
            check(new[] { "background", "output_compression", "moderation", "input_fidelity", "mask" }.All(f => !defaultBody.Contains(f)) &&
                (mode == "responses" || !defaultBody.Contains("user")), mode + " omits all new fields when absent");

            foreach (var format in new[] { "jpeg", "webp" })
            foreach (var compression in new[] { "0", "100" })
            {
                string[] optional = ["--background", "opaque", "--output-compression", compression, "--moderation", "low", "--format", format];
                if (mode != "responses") optional = [.. optional, "--user", "离线 user=42"];
                if (mode == "edit") optional = [.. optional, "--mask", source];
                var parsed = await CommandLineParser.ParseAsync(Args([.. modeArgs, .. optional]));
                using var request = RequestFactory.Create(parsed);
                if (mode == "edit")
                {
                    var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                    check(await parts["background"].ReadAsStringAsync() == "opaque" &&
                        await parts["output_compression"].ReadAsStringAsync() == compression &&
                        await parts["moderation"].ReadAsStringAsync() == "low" &&
                        await parts["user"].ReadAsStringAsync() == "离线 user=42" &&
                        !parts.ContainsKey("input_fidelity"), $"edit optional multipart fields {format}/{compression}");
                    check(parts["mask"].Headers.ContentType!.MediaType == "image/png" &&
                        (await parts["mask"].ReadAsByteArrayAsync()).SequenceEqual(png) &&
                        parts["mask"].Headers.ContentDisposition!.FileName is not null, "mask MIME, filename and original bytes");
                }
                else
                {
                    using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var body = document.RootElement;
                    var fields = mode == "responses" ? body.GetProperty("tools")[0] : body;
                    check(fields.GetProperty("background").GetString() == "opaque" &&
                        fields.GetProperty("output_compression").GetInt32() == int.Parse(compression) &&
                        fields.GetProperty("moderation").GetString() == "low" && !fields.TryGetProperty("input_fidelity", out _),
                        $"{mode} optional JSON fields {format}/{compression}");
                    check(mode == "responses" ? !body.TryGetProperty("background", out _) && !body.TryGetProperty("user", out _) :
                        body.GetProperty("user").GetString() == "离线 user=42", mode + " optional fields at correct level");
                }
            }
        }
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            check(true, "mask and reference handles released");

        // Parameter-stage I/O errors: no prompt provided, so the file must actually be read.
        async Task PromptFailure(string path, string name)
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response("{}")));
            var run = await RunJson(["--json", "--endpoint", "https://offline.invalid", "--api-key", "offline-only", "--prompt-file", path], handler);
            check(run.Exit == 2 && handler.Calls == 0 && run.Report.GetProperty("error").ValueKind == JsonValueKind.String, name);
        }
        await PromptFailure(Path.Combine(root, "missing-prompt.txt"), "missing prompt file exits 2");
        await PromptFailure(root, "directory prompt file exits 2");
        await PromptFailure("bad\0path", "invalid prompt path exits 2");
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await PromptFailure(source, "locked prompt file exits 2");

        var b64 = Convert.ToBase64String(png);
        foreach (var mode in new[] { "images", "edit", "responses" })
        {
            var target = Path.Combine(root, "json-" + mode + ".png");
            var payload = new Dictionary<string, object>
            {
                ["size"] = "1024x1024", ["quality"] = "high", ["output_format"] = "png",
                ["usage"] = new { input_tokens = 12, output_tokens = 34, total_tokens = 46, input_tokens_details = new { text_tokens = 2, image_tokens = 10 }, api_key = "offline-secret-not-for-report", b64_json = b64 }
            };
            payload[mode == "responses" ? "output" : "data"] = mode == "responses"
                ? new object[] { new { type = "image_generation_call", result = b64, size = "1024x1024", quality = "high", output_format = "png" } }
                : new object[] { new { b64_json = b64 } };
            using var handler = new FakeHandler(async request =>
            {
                check(request.Method == HttpMethod.Post && request.Headers.Authorization?.Parameter == "offline-secret-not-for-report", mode + " authenticated POST");
                await request.Content!.ReadAsByteArrayAsync();
                return Response(JsonSerializer.Serialize(payload));
            });
            string[] extra = mode == "edit" ? ["--image", source, "--mask", source] : [];
            var run = await RunJson(Args(["--json", "--mode", mode, "--output", target, .. extra]), handler);
            var report = run.Report;
            check(run.Exit == 0 && report.GetProperty("ok").GetBoolean() && report.GetProperty("exit_code").GetInt32() == 0 &&
                report.GetProperty("error").ValueKind == JsonValueKind.Null && report.GetProperty("mode").GetString() == mode, mode + " JSON success status");
            check(report.GetProperty("http_status").GetInt32() == 200 && report.GetProperty("request_id").GetString() == "request-offline" &&
                report.GetProperty("elapsed_ms").GetInt64() >= 0 && handler.Calls == 1, mode + " HTTP metadata and single request");
            check(report.GetProperty("files")[0].GetString() == target && (await File.ReadAllBytesAsync(target)).SequenceEqual(png), mode + " JSON files match disk");
            check(report.GetProperty("usage").GetProperty("total_tokens").GetInt32() == 46 &&
                report.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("image_tokens").GetInt32() == 10, mode + " numeric usage preserved");
            var metadata = report.GetProperty("response_metadata")[0];
            check(metadata.GetProperty("size").GetString() == "1024x1024" && metadata.GetProperty("quality").GetString() == "high" &&
                metadata.GetProperty("output_format").GetString() == "png", mode + " returned image metadata preserved");
            check(!run.Stdout.Contains(b64) && !run.Stdout.Contains("offline-secret-not-for-report") && !run.Stdout.Contains("b64_json") &&
                run.Stderr.Contains("POST ") && run.Stderr.Contains(target), mode + " JSON excludes secrets and image data; human progress on stderr");
        }

        foreach (var (status, body) in new[] {
            (200, "{}"), (200, "not JSON"), (200, "null"), (200, "[]"), (200, "{\"data\":[null]}"),
            (200, "{\"data\":[{\"b64_json\":\"%%%bad-base64\"}]}"),
            (401, "{\"error\":\"offline-secret-not-for-report\",\"b64_json\":\"" + b64 + "\"}"),
            (429, "rate limited"), (500, "server error") })
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response(body, status)));
            var run = await RunJson(Args("--json", "--mode", "images", "--output", Path.Combine(root, "failure.png")), handler);
            check(run.Exit == 1 && !run.Report.GetProperty("ok").GetBoolean() && run.Report.GetProperty("exit_code").GetInt32() == 1 &&
                run.Report.GetProperty("http_status").GetInt32() == status && run.Report.GetProperty("request_id").GetString() == "request-offline" &&
                run.Report.GetProperty("files").GetArrayLength() == 0 && run.Report.GetProperty("error").ValueKind == JsonValueKind.String && handler.Calls == 1,
                $"HTTP {status} / {body[..Math.Min(24, body.Length)]} stable failure, headers retained, no retry");
            check(!run.Stdout.Contains(b64) && !run.Stdout.Contains("offline-secret-not-for-report"), "failure JSON contains no response payload or key");
        }
        foreach (var header in new[] { "apim-request-id", "x-ms-request-id" })
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response("{}", header: header)));
            var run = await RunJson(Args("--json"), handler);
            check(run.Report.GetProperty("request_id").GetString() == "request-offline", "Azure request ID fallback " + header);
        }
        foreach (var exception in new Exception[] { new HttpRequestException("offline network failure"), new TaskCanceledException("offline timeout"), new IOException("offline read failure") })
        {
            using var handler = new FakeHandler(_ => Task.FromException<HttpResponseMessage>(exception));
            var run = await RunJson(Args("--json"), handler);
            check(run.Exit == 1 && handler.Calls == 1 && run.Report.GetProperty("http_status").ValueKind == JsonValueKind.Null &&
                run.Report.GetProperty("error").ValueKind == JsonValueKind.String, exception.GetType().Name + " single JSON, no retry");
        }
        using (var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ThrowingContent() })))
        {
            var run = await RunJson(Args("--json"), handler);
            check(run.Exit == 1 && run.Report.GetProperty("http_status").GetInt32() == 200, "body read failure retains HTTP status");
        }

        var successBody = JsonSerializer.Serialize(new { data = new[] { new { b64_json = b64 } } });
        var blockedOutput = Path.Combine(root, "blocked.png");
        await File.WriteAllBytesAsync(blockedOutput, png);
        foreach (var path in new[] { Path.Combine(blockedOutput, "child.png"), "bad\0output.png" })
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response(successBody)));
            var run = await RunJson(Args("--json", "--output", path), handler);
            check(run.Exit == 1 && run.Report.GetProperty("files").GetArrayLength() == 0, "output I/O failure is stable, no false saved file");
        }
        using (var handler = new FakeHandler(_ => Task.FromResult(Response(JsonSerializer.Serialize(new { data = new[] { new { b64_json = b64 }, new { b64_json = "%%%bad" } } })))))
        {
            var run = await RunJson(Args("--json", "--output", Path.Combine(root, "partial.png")), handler);
            check(run.Exit == 1 && run.Report.GetProperty("files").GetArrayLength() == 1 &&
                File.Exists(run.Report.GetProperty("files")[0].GetString()), "partial write lists only successfully saved files");
        }

        using (var handler = new FakeHandler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }
            : Response("{\"data\":[{\"url\":\"https://offline.invalid/image.png\"}]}"))))
        {
            var run = await RunJson(Args("--json", "--output", Path.Combine(root, "url.png")), handler);
            check(run.Exit == 0 && handler.Calls == 2 && run.Report.GetProperty("files").GetArrayLength() == 1, "existing URL image mode still supported offline");
        }
        using (var handler = new FakeHandler(_ => Task.FromResult(Response(successBody))))
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var path = Path.Combine(root, "legacy.png");
            var exit = await CliApplication.RunAsync(Args("--output", path), stdout, stderr, handler);
            check(exit == 0 && stdout.ToString().StartsWith("POST ") && stdout.ToString().Contains(path) && stderr.ToString() == "", "non-JSON output remains compatible");
        }
        using (var handler = new FakeHandler(_ => Task.FromResult(Response("{\"data\":[{\"b64_json\":\"%%%\"}]}"))))
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await CliApplication.RunAsync(Args(), stdout, stderr, handler);
            check(exit == 1 && stderr.ToString().Length > 0, "non-JSON bad base64 exits 1 without crashing");
        }
        using (var handler = new FakeHandler(_ => Task.FromResult(Response("{}"))))
        {
            var run = await RunJson(["--JSON", "--help"], handler);
            check(run.Exit == 0 && handler.Calls == 0 && run.Stderr.Contains("--mask") && run.Report.GetProperty("ok").GetBoolean(), "JSON help uses stderr and sends no HTTP");
            foreach (var args in new[] { Array.Empty<string>(), new[] { "-h" }, new[] { "help" }, new[] { "--help" } })
            {
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();
                check(await CliApplication.RunAsync(args, stdout, stderr, handler) == 0 && stdout.ToString().Contains("用法:"), "legacy help alias " + string.Join(' ', args));
            }
        }
    }

    private static HttpResponseMessage Response(string body, int status = 200, string header = "x-request-id")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
        response.Headers.Add(header, "request-offline");
        return response;
    }

    private static async Task<(int Exit, string Stdout, string Stderr, JsonElement Report)> RunJson(string[] args, FakeHandler handler)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(args, output, error, handler);
        using var document = JsonDocument.Parse(output.ToString()); // Rejects extra text or a second JSON document.
        return (exit, output.ToString(), error.ToString(), document.RootElement.Clone());
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return respond(request);
        }
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.FromException(new IOException("offline body failure"));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}