using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TrueFluentPro.Models.EndpointProfiles;

public sealed class EndpointProfileDefinition
{
    public string Id { get; set; } = "";
    public string Vendor { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EndpointApiType EndpointType { get; set; } = EndpointApiType.OpenAiCompatible;

    public string DisplayName { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string Summary { get; set; } = "";
    public string DefaultNamePrefix { get; set; } = "";
    public string IconAssetPath { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public EndpointProfileDefaults Defaults { get; set; } = new();
    public EndpointProfileAuthSettings Auth { get; set; } = new();
    public EndpointProfileModelDiscoverySettings ModelDiscovery { get; set; } = new();
    public EndpointProfileTextSettings Text { get; set; } = new();
    public EndpointProfileAudioSettings Audio { get; set; } = new();
    public EndpointProfileSpeechSettings Speech { get; set; } = new();
    public EndpointProfileImageSettings Image { get; set; } = new();
    public EndpointProfileVideoSettings Video { get; set; } = new();
}
