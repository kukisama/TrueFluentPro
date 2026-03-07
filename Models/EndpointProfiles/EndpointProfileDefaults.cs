using System.Text.Json.Serialization;

namespace TrueFluentPro.Models.EndpointProfiles;

public sealed class EndpointProfileDefaults
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AzureAuthMode AuthMode { get; set; } = AzureAuthMode.ApiKey;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApiKeyHeaderMode ApiKeyHeaderMode { get; set; } = ApiKeyHeaderMode.Auto;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TextApiProtocolMode TextApiProtocolMode { get; set; } = TextApiProtocolMode.Auto;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ImageApiRouteMode ImageApiRouteMode { get; set; } = ImageApiRouteMode.Auto;

    public string ApiVersion { get; set; } = "";
    public bool SupportsAad { get; set; }
    public bool ClearAzureIdentityFields { get; set; }
}
