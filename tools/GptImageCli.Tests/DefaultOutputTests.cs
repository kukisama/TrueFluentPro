using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GptImageCli;
using CommandLineParser = OfflineCli;
using CliApplication = OfflineCli;

internal static class DefaultOutputTests
{
    public static async Task RunAsync(string root, string source, byte[] png, Action<bool, string> check)
    {
        // Program has already cleared inherited configuration. Only synthetic values are used here.
        Environment.SetEnvironmentVariable("GPT_IMAGE_ENDPOINT", "https://offline.invalid");
        Environment.SetEnvironmentVariable("GPT_IMAGE_API_KEY", "offline-defaults-only");
        var originalDirectory = Directory.GetCurrentDirectory();
        var cwd = Directory.CreateDirectory(Path.Combine(root, "默认工作目录.v1")).FullName;
        try
        {
            Directory.SetCurrentDirectory(cwd);
            var defaults = new CliOptions { Endpoint = "https://offline.invalid", ApiKey = "offline-defaults-only", Prompt = "中文提示词" };
            var parsed = await CommandLineParser.ParseAsync(["--prompt", defaults.Prompt]);
            check(parsed.Mode == defaults.Mode && defaults.Mode == ApiMode.Images, "parser/options default Images agree");
            check(parsed.Size == defaults.Size && defaults.Size == "1024x640", "parser/options default size agree");
            check(parsed.Quality == defaults.Quality && defaults.Quality == "medium", "parser/options default quality agree");
            check(parsed.OutputFormat == defaults.OutputFormat && defaults.OutputFormat == "png", "parser/options default format agree");
            check(parsed.Count == defaults.Count && defaults.Count == 1, "parser/options default count agree");
            check(parsed.OutputPath == defaults.OutputPath && defaults.OutputPath == ".", "parser/options default cwd agree");
            check(parsed.AuthMode == defaults.AuthMode && defaults.AuthMode == AuthMode.Bearer, "parser/options default auth agree for non-Azure endpoint");
            check(parsed.TextModel == defaults.TextModel && defaults.TextModel == "gpt-4.1" &&
                parsed.ImageModel == defaults.ImageModel && defaults.ImageModel == "gpt-image-2", "parser/options default models agree");
            check(parsed.TimeoutMinutes == defaults.TimeoutMinutes && defaults.TimeoutMinutes == 10 &&
                parsed.Overwrite == defaults.Overwrite && !defaults.Overwrite, "parser/options timeout and non-overwrite agree");
            check(parsed.ReferenceImagePaths.Count == 0 && defaults.ReferenceImagePaths.Count == 0 &&
                parsed.ApiVersion == defaults.ApiVersion && defaults.ApiVersion is null &&
                parsed.MaskPath == defaults.MaskPath && defaults.MaskPath is null &&
                parsed.Background == defaults.Background && defaults.Background is null &&
                parsed.OutputCompression == defaults.OutputCompression && defaults.OutputCompression is null &&
                parsed.Moderation == defaults.Moderation && defaults.Moderation is null &&
                parsed.User == defaults.User && defaults.User is null, "parser/options absent optional defaults agree");

            async Task<(int Exit, string[] Files, string Text)> Run(bool json, int count = 1, params string[] extra)
            {
                using var handler = new FakeHandler(async request =>
                {
                    check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/images/generations" &&
                        request.Headers.Authorization?.Parameter == "offline-defaults-only" &&
                        !request.Headers.Contains("x-ms-oai-image-generation-deployment"), "minimal request actually sends authenticated Images POST");
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var fields = body.RootElement;
                    foreach (var (name, expected) in new[] { ("model", "gpt-image-2"), ("prompt", defaults.Prompt),
                        ("size", "1024x640"), ("quality", "medium"), ("output_format", "png") })
                        check(fields.GetProperty(name).GetString() == expected, "minimal request default field " + name);
                    check(count == 1 ? !fields.TryGetProperty("n", out _) : fields.GetProperty("n").GetInt32() == count,
                        "default n=1 stays omitted; explicit multi-count is sent");
                    check(fields.EnumerateObject().Count() == (count == 1 ? 5 : 6), "minimal request has no Responses or optional fields");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                        { data = Enumerable.Range(0, count).Select(_ => new { b64_json = Convert.ToBase64String(png) }) })) };
                });
                using var output = new StringWriter();
                using var error = new StringWriter();
                string[] args = ["--prompt", defaults.Prompt, .. extra];
                if (json) args = [.. args, "--json"];
                var exit = await CliApplication.RunAsync(args, output, error, handler);
                var text = output.ToString();
                if (!json)
                {
                    check(exit == 0 && handler.Calls == 1 && error.ToString() == "" && text.StartsWith("POST "), "prompt-only invocation succeeds without JSON");
                    return (exit, text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1..], text);
                }
                using var document = JsonDocument.Parse(text);
                var report = document.RootElement;
                check(report.GetProperty("mode").GetString() == "images" && report.GetProperty("exit_code").GetInt32() == exit,
                    "minimal JSON reports actual images mode and exit code");
                check(handler.Calls == (exit == 2 ? 0 : 1), "output conflicts checked internally before HTTP; no retries");
                return (exit, report.GetProperty("files").EnumerateArray().Select(p => p.GetString()!).ToArray(), text);
            }

            async Task VerifyFiles(string[] files, string directory, int count)
            {
                check(files.Length == count && files.All(p => Path.GetDirectoryName(p) == Path.GetFullPath(directory)), "auto output uses requested directory (including Chinese paths)");
                foreach (var path in files)
                {
                    check(Regex.IsMatch(Path.GetFileName(path), @"\Agpt-image-\d{8}-\d{6}-[0-9a-f]{32}-\d{2}\.png\z"), "auto filename contains timestamp AND GUID");
                    check((await File.ReadAllBytesAsync(path)).SequenceEqual(png), "auto file preserves response bytes");
                }
            }

            var first = await Run(false);
            var second = await Run(true);
            await VerifyFiles(first.Files, cwd, 1);
            await VerifyFiles(second.Files, cwd, 1);
            check(first.Files[0] != second.Files[0] && Directory.GetFiles(cwd).Length == 2, "repeated omitted output produces unique cwd files without probes");

            Directory.CreateDirectory("已有.目录");
            foreach (var path in new[] { ".", "自动创建/中文目录", "已有.目录", "新建.目录" + Path.DirectorySeparatorChar,
                "另一个.目录" + Path.AltDirectorySeparatorChar })
            {
                var run = await Run(true, 1, "--output", path);
                check(run.Exit == 0 && Directory.Exists(path), "directory path succeeds without caller preflight: " + path);
                await VerifyFiles(run.Files, Path.TrimEndingDirectorySeparator(path), 1);
            }
            var multi = await Run(true, 2, "--output", "多图.目录/", "--n", "2");
            check(multi.Exit == 0, "multi-image directory automatically created");
            await VerifyFiles(multi.Files, "多图.目录", 2);
            check(multi.Files[0][..^7] == multi.Files[1][..^7] && multi.Files[0].EndsWith("-01.png") && multi.Files[1].EndsWith("-02.png"),
                "directory multi-images share GUID and use numbered suffixes");

            foreach (var count in new[] { 1, 2 })
            {
                var path = $"显式子目录/保留.名字{count}.png";
                var run = await Run(true, count, "--output", path, "--n", count.ToString());
                var expected = Enumerable.Range(1, count).Select(i => Path.GetFullPath(count == 1 ? path :
                    Path.Combine("显式子目录", $"保留.名字{count}-{i:00}.png"))).ToArray();
                check(run.Exit == 0 && run.Files.SequenceEqual(expected), "explicit Chinese filename preserved, numbering only for multiple images");
                var conflict = await Run(true, count, "--output", path, "--n", count.ToString());
                check(conflict.Exit == 2 && conflict.Files.Length == 0, "default Images refuses explicit filename conflict before sending");
                foreach (var file in expected)
                    check((await File.ReadAllBytesAsync(file)).SequenceEqual(png), "conflict leaves explicit output unchanged");
            }

            var edit = await CommandLineParser.ParseAsync(["--mode", "edit", "--image", source, "--prompt", defaults.Prompt]);
            using (var request = RequestFactory.Create(edit))
            {
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                check(request.RequestUri!.AbsolutePath == "/v1/images/edits" && edit.OutputPath == ".", "minimal edit keeps edit route and cwd default");
                foreach (var (name, expected) in new[] { ("size", "1024x640"), ("quality", "medium"), ("output_format", "png"), ("n", "1") })
                    check(await parts[name].ReadAsStringAsync() == expected, "minimal edit default " + name);
            }
            var responses = await CommandLineParser.ParseAsync(["--mode", "responses", "--size", "auto", "--prompt", defaults.Prompt]);
            using (var request = RequestFactory.Create(responses))
            using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                check(request.RequestUri!.AbsolutePath == "/v1/responses" && body.RootElement.GetProperty("tools")[0].GetProperty("size").GetString() == "auto",
                    "explicit Responses retains caller-supplied size");

            using var help = new StringWriter();
            Usage.Write(help);
            check(help.ToString().Contains("默认 images") && help.ToString().Contains("默认 1024x640") &&
                help.ToString().Contains("无需调用方生成 ID 或预先检查路径") && help.ToString().Contains("自行通过 --size 适配"),
                "Usage documents defaults, automatic output and explicit Responses size adaptation");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            Environment.SetEnvironmentVariable("GPT_IMAGE_ENDPOINT", null);
            Environment.SetEnvironmentVariable("GPT_IMAGE_API_KEY", null);
        }
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