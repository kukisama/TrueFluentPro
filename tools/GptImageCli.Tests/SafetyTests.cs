using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GptImageCli;

internal static class SafetyTests
{
    private const string Key = "offline_secret_for_safety";
    private static string[] Args(params string[] extra) =>
        ["--endpoint", "https://offline.invalid", "--api-key", Key, "--prompt", "offline", .. extra];

    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        var b64 = Convert.ToBase64String(png);
        foreach (var status in new[] { 400, 401, 429, 500 })
        {
            var body = JsonSerializer.Serialize(new { error = new { code = "Raw-New_Code.42", type = "invalid_request_error", param = "tools[0].output_format", message = Key } });
            using var handler = new FakeHandler(_ => Task.FromResult(Response(body, status)));
            var run = await RunJson(Args("--json"), handler);
            var api = run.Report.GetProperty("api_error");
            check(run.Exit == 1 && handler.Calls == 1 && api.GetProperty("code").GetString() == "Raw-New_Code.42" &&
                api.GetProperty("type").GetString() == "invalid_request_error" && api.GetProperty("param").GetString() == "tools[0].output_format",
                $"HTTP {status} preserves raw error identifiers without retry");
            check(api.GetProperty("message").GetString() == "服务端返回错误。" && !run.Text.Contains(Key) &&
                run.Report.GetProperty("http_status").GetInt32() == status && run.Report.GetProperty("error").ValueKind == JsonValueKind.String,
                $"HTTP {status} generic error does not replace api_error or expose server message");
        }

        foreach (var value in new object?[] { null, 42, true, new { code = "nested" }, new[] { "array" }, "", " ",
            new string('a', 65), new string('b', 20000), "line\nbreak", "newline\n", "中文", "<script>", "a/b", "a+b", "a=b", "a\\b", "a\"b", b64, Key, "prefix_" + Key })
        {
            var report = new CliReport();
            report.CaptureBody(JsonSerializer.Serialize(new { error = new { code = value, type = value, param = value, message = Key, b64_json = b64 } }), Key);
            using var document = JsonDocument.Parse(report.Serialize(1));
            var api = document.RootElement.GetProperty("api_error");
            check(new[] { "code", "type", "param" }.All(n => api.GetProperty(n).ValueKind == JsonValueKind.Null) &&
                !report.Serialize(1).Contains(Key) && !report.Serialize(1).Contains(b64), "unsafe/non-string error fields rejected, not truncated: " + (value?.GetType().Name ?? "null"));
        }
        foreach (var value in new[] { "x", new string('x', 64), "image[0]", "custom.future-code_9" })
        {
            var report = new CliReport();
            report.CaptureBody(JsonSerializer.Serialize(new { error = new { code = value, type = value, param = value } }));
            using var document = JsonDocument.Parse(report.Serialize(1));
            check(new[] { "code", "type", "param" }.All(n => document.RootElement.GetProperty("api_error").GetProperty(n).GetString() == value), "safe error identifier kept verbatim: " + value);
        }
        foreach (var body in new[] { "{}", "null", "[]", "not JSON", "{\"error\":null}", "{\"error\":\"text\"}",
            "{\"error\":[]}", "{\"output\":[{\"error\":{\"code\":\"nested\"}}]}", "{\"data\":[{\"error\":{\"code\":\"nested\"}}]}" })
        {
            var report = new CliReport();
            report.CaptureBody("{\"error\":{\"code\":\"previous\"}}");
            report.CaptureBody(body);
            using var document = JsonDocument.Parse(report.Serialize(1));
            check(document.RootElement.GetProperty("api_error").ValueKind == JsonValueKind.Null, "api_error only from current root.error object: " + body);
        }
        foreach (var status in new[] { 200, 400, 500 })
        foreach (var json in new[] { false, true })
        {
            var body = JsonSerializer.Serialize(new { error = new { code = Key, type = b64, param = new string('x', 10000), message = Key + b64 }, secret = Key });
            using var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                { Content = new StringContent(body), ReasonPhrase = Key }));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exit = await CliApplication.RunAsync(Args(json ? ["--json"] : []), output, error, handler);
            var text = output.ToString() + error;
            check(exit == 1 && handler.Calls == 1 && !text.Contains(Key) && !text.Contains(b64) && !text.Contains(new string('x', 65)),
                $"malicious body/reason/message not echoed on either stream: {status}, json={json}");
        }

        foreach (var extra in new[] { new[] { "--overwrite=true" }, new[] { "--overwrite=false" }, new[] { "--overwrite=" }, new[] { "--overwrite", "false" } })
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response("{}")));
            var run = await RunJson(Args(["--json", .. extra]), handler);
            check(run.Exit == 2 && handler.Calls == 0, "overwrite rejects a value before HTTP: " + string.Join(' ', extra));
        }
        check(!(await CommandLineParser.ParseAsync(Args())).Overwrite && (await CommandLineParser.ParseAsync(Args("--OVERWRITE"))).Overwrite,
            "overwrite defaults false and accepts case-insensitive valueless switch");
        using (var help = new StringWriter())
        {
            Usage.Write(help);
            check(help.ToString().Contains("--overwrite") && help.ToString().Contains("api_error") && help.ToString().Contains("GUID"), "Usage describes both safety changes");
        }

        var oldBytes = Enumerable.Repeat((byte)42, png.Length + 30).ToArray();
        foreach (var mode in new[] { "images", "edit" })
        foreach (var count in new[] { 1, 2, 3 })
        {
            var path = Path.Combine(root, $"precheck-{mode}-{count}.png");
            var existing = Target(path, count, count);
            await File.WriteAllBytesAsync(existing, oldBytes);
            using var handler = new FakeHandler(_ => Task.FromResult(Response(Images(b64, count)))) ;
            string[] extra = mode == "edit" ? ["--image", source] : [];
            var run = await RunJson(Args(["--json", "--mode", mode, "--n", count.ToString(), "--output", path, .. extra]), handler);
            check(run.Exit == 2 && handler.Calls == 0 && !run.Text.Contains("POST ") && run.Report.GetProperty("http_status").ValueKind == JsonValueKind.Null &&
                run.Report.GetProperty("files").GetArrayLength() == 0, $"{mode} n={count} checks every target before HTTP");
            check((await File.ReadAllBytesAsync(existing)).SequenceEqual(oldBytes) && (count == 1 || !File.Exists(Target(path, count, 1))),
                $"{mode} n={count} precheck has no writes and preserves existing file");
        }
        var directoryTarget = Path.Combine(root, "precheck-directory.png");
        Directory.CreateDirectory(directoryTarget);
        foreach (var path in new[] { directoryTarget, "bad\0target.png" })
        {
            using var handler = new FakeHandler(_ => Task.FromResult(Response(Images(b64, 1))));
            var run = await RunJson(Args("--json", "--mode", "images", "--output", path), handler);
            check(run.Exit == 2 && handler.Calls == 0, "precheck directory/invalid target exits 2 without POST");
        }

        foreach (var mode in new[] { "images", "edit", "responses" })
        foreach (var count in new[] { 1, 2 })
        {
            var path = Path.Combine(root, $"overwrite-{mode}-{count}.png");
            for (var i = 1; i <= count; i++) await File.WriteAllBytesAsync(Target(path, count, i), oldBytes);
            using var handler = new FakeHandler(async request =>
            {
                check(!(await request.Content!.ReadAsStringAsync()).Contains("overwrite"), "overwrite is local-only, not an API parameter");
                return Response(Images(b64, count, mode));
            });
            string[] extra = mode == "edit" ? ["--image", source] : [];
            var run = await RunJson(Args(["--json", "--mode", mode, "--n", count.ToString(), "--output", path, "--overwrite", .. extra]), handler);
            check(run.Exit == 0 && handler.Calls == 1 && run.Report.GetProperty("files").GetArrayLength() == count,
                $"explicit overwrite succeeds: {mode}, n={count}");
            for (var i = 1; i <= count; i++)
                check((await File.ReadAllBytesAsync(Target(path, count, i))).SequenceEqual(png), $"overwrite replaces and truncates {mode} file {i}/{count}");
        }

        foreach (var mode in new[] { "images", "edit", "responses" })
        foreach (var count in new[] { 1, 2 })
        {
            var path = Path.Combine(root, $"race-{mode}-{count}.png");
            var conflict = Target(path, count, count);
            using var handler = new FakeHandler(async _ =>
            {
                // Deterministically create a competing file AFTER preflight and BEFORE saving.
                await File.WriteAllBytesAsync(conflict, oldBytes);
                return Response(Images(b64, count, mode));
            });
            string[] extra = mode == "edit" ? ["--image", source] : [];
            var run = await RunJson(Args(["--json", "--mode", mode, "--n", (mode == "responses" ? 1 : count).ToString(), "--output", path, .. extra]), handler);
            var files = run.Report.GetProperty("files");
            check(run.Exit == 1 && handler.Calls == 1 && run.Report.GetProperty("http_status").GetInt32() == 200 && files.GetArrayLength() == count - 1,
                $"CreateNew stops post-preflight race, no retry: {mode}, actual count={count}");
            check((await File.ReadAllBytesAsync(conflict)).SequenceEqual(oldBytes), $"race does not truncate competing {mode} file");
            if (count == 2)
                check(files[0].GetString() == Target(path, count, 1) && (await File.ReadAllBytesAsync(files[0].GetString()!)).SequenceEqual(png),
                    $"{mode} preserves and reports completed partial files");
        }
        foreach (var count in new[] { 1, 2 })
        {
            var path = Path.Combine(root, $"responses-existing-{count}.png");
            var conflict = Target(path, count, count);
            await File.WriteAllBytesAsync(conflict, oldBytes);
            using var handler = new FakeHandler(_ => Task.FromResult(Response(Images(b64, count, "responses"))));
            var run = await RunJson(Args("--json", "--output", path), handler);
            check(run.Exit == 1 && handler.Calls == 1 && run.Report.GetProperty("files").GetArrayLength() == count - 1 &&
                (await File.ReadAllBytesAsync(conflict)).SequenceEqual(oldBytes), "Responses protects actual existing output count=" + count);
        }

        using var client = new HttpClient(new FakeHandler(_ => throw new Exception("No HTTP expected for base64 writing")));
        var directPath = Path.Combine(root, "direct-existing.png");
        await File.WriteAllBytesAsync(directPath, oldBytes);
        var saved = new List<string>();
        try { await ImageResultWriter.SaveAsync([new(b64, null)], directPath, "png", client, saved); check(false, "direct writer must refuse existing file"); }
        catch (IOException) { check(saved.Count == 0 && (await File.ReadAllBytesAsync(directPath)).SequenceEqual(oldBytes), "writer default independently protects existing file"); }
        await ImageResultWriter.SaveAsync([new(b64, null)], directPath, "png", client, saved, overwrite: true);
        check(saved.SequenceEqual(new[] { directPath }) && (await File.ReadAllBytesAsync(directPath)).SequenceEqual(png), "direct writer explicit overwrite succeeds");

        var outputDirectory = Path.Combine(root, "guid-output");
        var first = await ImageResultWriter.SaveAsync([new(b64, null), new(b64, null)], outputDirectory, "jpeg", client);
        var second = await ImageResultWriter.SaveAsync([new(b64, null)], outputDirectory, "jpeg", client);
        var all = first.Concat(second).ToArray();
        check(all.Distinct().Count() == 3 && Directory.GetFiles(outputDirectory).Length == 3 &&
            all.All(p => Regex.IsMatch(Path.GetFileName(p), @"\Agpt-image-\d{8}-\d{6}-[0-9a-f]{32}-\d{2}\.jpg\z")),
            "directory names contain GUID, preserve extension, leave no probe/temp files");
        foreach (var path in all) check((await File.ReadAllBytesAsync(path)).SequenceEqual(png), "GUID output bytes preserved");
    }

    private static string Target(string path, int count, int index) => count == 1 ? path :
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}-{index:00}{Path.GetExtension(path)}");

    private static string Images(string b64, int count, string mode = "images") => mode == "responses"
        ? JsonSerializer.Serialize(new { output = Enumerable.Range(0, count).Select(_ => new { type = "image_generation_call", result = b64 }) })
        : JsonSerializer.Serialize(new { data = Enumerable.Range(0, count).Select(_ => new { b64_json = b64 }) });

    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };

    private static async Task<(int Exit, string Text, JsonElement Report)> RunJson(string[] args, FakeHandler handler)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(args, output, error, handler);
        using var document = JsonDocument.Parse(output.ToString());
        return (exit, output.ToString() + error, document.RootElement.Clone());
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
}