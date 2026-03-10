using System.Collections.Generic;
using System.Text.Json.Serialization;
using TrueFluentPro.Models;

namespace TrueFluentPro.Models.EndpointProfiles;

public enum EndpointCapabilityPolicyMode
{
    InheritDefault,
    AllowOnly,
    DenySome
}

public sealed class EndpointCapabilityPolicy
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EndpointCapabilityPolicyMode Mode { get; set; } = EndpointCapabilityPolicyMode.InheritDefault;

    public List<string> Allowed { get; set; } = new();
    public List<string> Disabled { get; set; } = new();
}

public sealed class EndpointPlatformDefaultCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<EndpointPlatformDefaultPolicy> Policies { get; set; } = new();
}

public sealed class EndpointPlatformDefaultPolicy
{
    public string Id { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EndpointApiType EndpointType { get; set; } = EndpointApiType.OpenAiCompatible;

    public EndpointProfileDefaults Defaults { get; set; } = new();
    public EndpointPlatformDefaultAuthSettings Auth { get; set; } = new();
    public EndpointCapabilityPolicy Capabilities { get; set; } = new();
    public EndpointPlatformDefaultTextSettings Text { get; set; } = new();
    public EndpointPlatformDefaultModelDiscoverySettings ModelDiscovery { get; set; } = new();
    public EndpointPlatformDefaultAudioSettings Audio { get; set; } = new();
    public EndpointPlatformDefaultSpeechSettings Speech { get; set; } = new();
    public EndpointPlatformDefaultImageSettings Image { get; set; } = new();
    public EndpointPlatformDefaultVideoSettings Video { get; set; } = new();
}

public sealed class EndpointPlatformDefaultAuthSettings
{
    public string DefaultMode { get; set; } = "";
    public List<string> AllowedModes { get; set; } = new();
    public string DefaultApiKeyHeaderMode { get; set; } = "";
    public List<string> AllowedApiKeyHeaderModes { get; set; } = new();
}

public sealed class EndpointPlatformDefaultTextSettings
{
    public string PrimaryProtocol { get; set; } = "";
    public string PrimaryUrl { get; set; } = "";
    public string DeploymentPrimaryUrl { get; set; } = "";
}

public sealed class EndpointPlatformDefaultModelDiscoverySettings
{
    public string PrimaryUrl { get; set; } = "";
}

public sealed class EndpointPlatformDefaultAudioSettings
{
    public string PrimaryUrl { get; set; } = "";
    public string DefaultApiVersion { get; set; } = "";
}

public sealed class EndpointPlatformDefaultSpeechSettings
{
    public string PrimaryUrl { get; set; } = "";
    public string DefaultApiVersion { get; set; } = "";
}

public sealed class EndpointPlatformDefaultImageSettings
{
    public string GeneratePrimaryUrl { get; set; } = "";
    public string EditPrimaryUrl { get; set; } = "";
    public string DeploymentGeneratePrimaryUrl { get; set; } = "";
}

public sealed class EndpointPlatformDefaultVideoSettings
{
    public string DefaultMode { get; set; } = "";
    public string CreatePrimaryUrl { get; set; } = "";
    public string JobsCreatePrimaryUrl { get; set; } = "";
    public string PollPrimaryUrl { get; set; } = "";
    public string JobsPollPrimaryUrl { get; set; } = "";
    public string DownloadPrimaryUrl { get; set; } = "";
    public string JobsDownloadPrimaryUrl { get; set; } = "";
    public string DownloadVideoContentPrimaryUrl { get; set; } = "";
    public string GenerationDownloadPrimaryUrl { get; set; } = "";
    public string GenerationDownloadVideoContentPrimaryUrl { get; set; } = "";
}

public sealed class EndpointProfileOverrideBundle
{
    public EndpointProfileDefaults Defaults { get; set; } = new();
    public EndpointProfileOverrideAuthSettings Auth { get; set; } = new();
    public EndpointCapabilityPolicy Capabilities { get; set; } = new();
    public EndpointProfileRouteOverrideBundle Routes { get; set; } = new();
    public EndpointProfileVersionOverrideSettings Version { get; set; } = new();
}

public sealed class EndpointProfileOverrideAuthSettings
{
    public string DefaultMode { get; set; } = "";
    public List<string> AllowedModes { get; set; } = new();
    public string DefaultApiKeyHeaderMode { get; set; } = "";
    public List<string> AllowedApiKeyHeaderModes { get; set; } = new();
}

public sealed class EndpointProfileRouteOverrideBundle
{
    public EndpointPlatformDefaultTextSettings Text { get; set; } = new();
    public EndpointPlatformDefaultModelDiscoverySettings ModelDiscovery { get; set; } = new();
    public EndpointPlatformDefaultAudioSettings Audio { get; set; } = new();
    public EndpointPlatformDefaultSpeechSettings Speech { get; set; } = new();
    public EndpointPlatformDefaultImageSettings Image { get; set; } = new();
    public EndpointPlatformDefaultVideoSettings Video { get; set; } = new();
}

public sealed class EndpointProfileVersionOverrideSettings
{
    public string EndpointApiVersion { get; set; } = "";
    public string TextApiVersion { get; set; } = "";
    public string AudioApiVersion { get; set; } = "";
    public string SpeechApiVersion { get; set; } = "";
    public string VideoApiVersion { get; set; } = "";
}

public sealed class EndpointProfileFallbackBundle
{
    public List<string> ModelDiscovery { get; set; } = new();
    public List<string> Text { get; set; } = new();
    public List<string> ImageGenerate { get; set; } = new();
    public List<string> ImageEdit { get; set; } = new();
    public List<string> Audio { get; set; } = new();
    public List<string> Speech { get; set; } = new();
    public List<string> VideoCreate { get; set; } = new();
    public List<string> VideoJobsCreate { get; set; } = new();
    public List<string> VideoPoll { get; set; } = new();
    public List<string> VideoJobsPoll { get; set; } = new();
    public List<string> VideoDownload { get; set; } = new();
    public List<string> VideoJobsDownload { get; set; } = new();
    public List<string> VideoDownloadVideoContent { get; set; } = new();
    public List<string> VideoGenerationDownload { get; set; } = new();
    public List<string> VideoGenerationDownloadVideoContent { get; set; } = new();
}

public sealed class EndpointProfileSpecialPolicyBundle
{
    public bool AllowApimSubscriptionKeyQueryRetry { get; set; }
    public bool AllowPreviewFallback { get; set; }
}
