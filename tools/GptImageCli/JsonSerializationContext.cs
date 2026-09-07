using System.Text.Json;
using System.Text.Json.Serialization;

namespace GptImageCli;

// Keep the wire names and property order identical to the former anonymous objects.
internal sealed record ResponsesInput(string role, ResponsesContent[] content);
internal sealed record ResponsesContent(string type, string text);
internal sealed record ImageToolChoice(string type);
internal sealed record ApiErrorReport(string? code, string? type, string? param, string message);
internal sealed record CliReportData(
    bool ok, int exit_code, List<string> files, string? mode, int? http_status,
    string? request_id, long elapsed_ms, object? usage, ApiErrorReport? api_error,
    string? error, List<Dictionary<string, string>> response_metadata);

// Every concrete type stored in an object slot must be registered: request dictionaries,
// their scalar/array/DTO values, and NumericUsage's recursive dictionaries/JsonElements.
// Nullable annotations do not change Dictionary<string, object>'s runtime type.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, object>), TypeInfoPropertyName = "RequestBody")]
[JsonSerializable(typeof(Dictionary<string, object>[]))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(ResponsesInput))]
[JsonSerializable(typeof(ResponsesContent[]))]
[JsonSerializable(typeof(ResponsesContent))]
[JsonSerializable(typeof(ImageToolChoice))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(ApiErrorReport))]
[JsonSerializable(typeof(CliReportData))]
internal partial class JsonSerializationContext : JsonSerializerContext;