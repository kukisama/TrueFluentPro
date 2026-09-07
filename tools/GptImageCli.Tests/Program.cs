using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GptImageCli;

// Offline regression runner: no real configuration, credentials or cloud requests.
foreach (var variable in new[] { "GPT_IMAGE_ENDPOINT", "OPENAI_BASE_URL", "AZURE_OPENAI_ENDPOINT",
    "GPT_IMAGE_API_KEY", "OPENAI_API_KEY", "AZURE_OPENAI_API_KEY", "GPT_IMAGE_TEXT_MODEL", "GPT_IMAGE_MODEL", "AZURE_OPENAI_API_VERSION" })
    Environment.SetEnvironmentVariable(variable, null);
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gpt-image-cli-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "参考图 source.png");
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a7ioAAAAASUVORK5CYII=");
await File.WriteAllBytesAsync(source, png);
var checks = 0;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

string[] Arguments(params string[] extra) =>
    ["--endpoint", "http://127.0.0.1:1", "--api-key", "offline-only", "--prompt", "把帆船改为绿色", .. extra];

async Task Reject(string name, params string[] extra)
{
    using var output = new StringWriter();
    using var error = new StringWriter();
    var exit = await CliApplication.RunAsync(Arguments(extra), output, error);
    Check(exit == 2 && !output.ToString().Contains("POST "), name + " rejected before HTTP");
}

await Reject("edit missing image", "--mode", "edit");
await Reject("missing image value", "--mode", "edit", "--image");
await Reject("empty image value", "--mode", "edit", "--image=");
await Reject("nonexistent image", "--mode", "edit", "--image", Path.Combine(root, "missing.png"));
await Reject("directory as image", "--mode", "edit", "--image", root);
await Reject("generation must not ignore image", "--mode", "images", "--image", source);
await Reject("responses must not ignore image", "--mode", "responses", "--image", source);
await Reject("any missing reference", "--mode", "edit", "--image", source, "--image", Path.Combine(root, "missing.png"));
var textFile = Path.Combine(root, "invalid.txt");
await File.WriteAllTextAsync(textFile, "not an image");
await Reject("unsupported image extension", "--mode", "edit", "--image", textFile);

var options = await CommandLineParser.ParseAsync(Arguments("--mode", "edit", "--image", source,
    "--size", "1024x640", "--quality", "low", "--format", "jpg", "--n", "2", "--api-version", "preview"));
using (var request = RequestFactory.Create(options))
{
    Check(request.RequestUri!.PathAndQuery == "/v1/images/edits?api-version=preview", "edit route and api-version");
    Check(request.Content is MultipartFormDataContent, "multipart content, not JSON");
    Check(!request.Headers.Contains("x-ms-oai-image-generation-deployment"), "edit independent of Responses header");
    var parts = ((MultipartFormDataContent)request.Content!).ToList();
    var image = parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "image");
    Check(image.Headers.ContentType!.MediaType == "image/png", "source MIME");
    Check((await image.ReadAsByteArrayAsync()).SequenceEqual(png), "original image bytes preserved");
    foreach (var (name, value) in new[] { ("model", "gpt-image-2"), ("prompt", "把帆船改为绿色"),
        ("size", "1024x640"), ("quality", "low"), ("output_format", "jpeg"), ("n", "2") })
    {
        Check(await parts.Single(p => p.Headers.ContentDisposition!.Name!.Trim('"') == name).ReadAsStringAsync() == value, "edit field " + name);
    }
}
using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Check(true, "request disposal releases source file");

var multi = await CommandLineParser.ParseAsync(Arguments("--mode=edits", "--image=" + source, "--IMAGE", source));
using (var request = RequestFactory.Create(multi))
{
    Check(multi.ReferenceImagePaths.Count == 2, "repeated image arguments retained");
    Check(((MultipartFormDataContent)request.Content!).Count(p => p.Headers.ContentDisposition!.Name!.Trim('"') == "image[]") == 2,
        "multi-reference image[] fields");
}

foreach (var (endpoint, expected) in new[] {
    ("https://example.invalid", "/v1/images/edits"),
    ("https://example.invalid/v1", "/v1/images/edits"),
    ("https://example.invalid/openai", "/openai/v1/images/edits"),
    ("https://example.openai.azure.com", "/openai/v1/images/edits"),
    ("https://example.invalid/custom/images/edits?api-version=existing", "/custom/images/edits?api-version=existing") })
{
    using var request = RequestFactory.Create(new CliOptions {
        Endpoint = endpoint, ApiKey = "offline-only", Prompt = "edit", Mode = ApiMode.Edit, ReferenceImagePaths = [source] });
    Check(request.RequestUri!.PathAndQuery == expected, "URL " + expected);
}

foreach (var mode in new[] { "images", "responses" })
{
    var parsed = await CommandLineParser.ParseAsync(Arguments("--mode", mode));
    using var request = RequestFactory.Create(parsed);
    using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Check(request.Content.Headers.ContentType!.MediaType == "application/json", mode + " stays JSON");
    Check(request.RequestUri!.AbsolutePath == (mode == "images" ? "/v1/images/generations" : "/v1/responses"), mode + " route unchanged");
    Check(json.RootElement.TryGetProperty(mode == "images" ? "prompt" : "tools", out _), mode + " body preserved");
}
Check((await CommandLineParser.ParseAsync(Arguments())).Mode == ApiMode.Responses, "default mode preserved");

// Actual CLI HTTP -> parsing -> file writing, using only a loopback fake server.
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
async Task<string> ServeAsync()
{
    using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
    using var stream = connection.GetStream();
    var headerBytes = new List<byte>();
    var one = new byte[1];
    while (true)
    {
        if (await stream.ReadAsync(one, timeout.Token) == 0) throw new IOException("Unexpected EOF");
        headerBytes.Add(one[0]);
        if (headerBytes.Count >= 4 && headerBytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        if (headerBytes.Count > 32768) throw new IOException("Header too large");
    }
    var headers = Encoding.ASCII.GetString(headerBytes.ToArray());
    var lengthLine = headers.Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var body = new byte[int.Parse(lengthLine.Split(':')[1].Trim())];
    await stream.ReadExactlyAsync(body, timeout.Token);
    var response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data = new[] { new { b64_json = Convert.ToBase64String(png) } } }));
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
    await stream.WriteAsync(response, timeout.Token);
    return headers + Encoding.UTF8.GetString(body);
}
var server = ServeAsync();
var target = Path.Combine(root, "edited.png");
using var cliOutput = new StringWriter();
using var cliError = new StringWriter();
var result = await CliApplication.RunAsync(Arguments("--endpoint", $"http://127.0.0.1:{port}", "--mode", "edit",
    "--auth", "api-key", "--image", source, "--output", target), cliOutput, cliError);
var wire = await server;
Check(result == 0 && cliError.ToString() == "", "CLI loopback edit exit success");
Check(wire.StartsWith("POST /v1/images/edits") && wire.Contains("api-key: offline-only"), "wire route and authentication");
Check(wire.Contains("multipart/form-data;") && wire.Contains("image/png"), "wire multipart and image MIME");
Check((await File.ReadAllBytesAsync(target)).SequenceEqual(png), "HTTP image response saved unchanged");
Console.WriteLine($"Original regression checks passed: {checks}");
await FeatureTests.RunAsync(root, source, png, Check);
await SafetyTests.RunAsync(root, source, png, Check);
Console.WriteLine($"All {checks} checks passed. Artifacts: {root}");