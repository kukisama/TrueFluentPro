using System.Collections.Generic;

namespace TrueFluentPro.Models.EndpointProfiles;

public sealed class EndpointProfileAuthSettings
{
    public List<string> SupportedModes { get; set; } = new();
    public List<string> SupportedApiKeyHeaderModes { get; set; } = new();
    public string DefaultMode { get; set; } = "";
    public string DefaultApiKeyHeaderMode { get; set; } = "";
    public bool SupportsSubscriptionKeyQueryFallback { get; set; }
}

public sealed class EndpointProfileModelDiscoverySettings
{
    public List<string> UrlCandidates { get; set; } = new();
}

public sealed class EndpointProfileTextSettings
{
    public string PreferredProtocol { get; set; } = "";
    public bool AppendApiVersionWhenPresent { get; set; }
    public List<string> DeploymentChatCompletionsUrlCandidates { get; set; } = new();
    public List<string> ResponsesUrlCandidates { get; set; } = new();
    public List<string> ChatCompletionsV1UrlCandidates { get; set; } = new();
    public List<string> ChatCompletionsRawUrlCandidates { get; set; } = new();
}

public sealed class EndpointProfileImageSettings
{
    public bool AppendApiVersionWhenPresent { get; set; }
    public List<string> GenerateUrlCandidates { get; set; } = new();
    public List<string> EditUrlCandidates { get; set; } = new();
    public List<string> DeploymentGenerateUrlCandidates { get; set; } = new();
}

public sealed class EndpointProfileVideoSettings
{
    public List<EndpointProfileVideoApiModeOption> ApiModeOptions { get; set; } = new();
    public List<string> SupportedApiModes { get; set; } = new();
    public bool AppendApiVersionWhenPresent { get; set; }
    public List<string> CreateUrlCandidates { get; set; } = new();
    public List<string> PollUrlCandidates { get; set; } = new();
    public List<string> DownloadUrlCandidates { get; set; } = new();
    public List<string> DownloadVideoContentUrlCandidates { get; set; } = new();
    public List<string> JobsCreateUrlCandidates { get; set; } = new();
    public List<string> JobsPollUrlCandidates { get; set; } = new();
    public List<string> JobsDownloadUrlCandidates { get; set; } = new();
    public List<string> GenerationDownloadUrlCandidates { get; set; } = new();
    public List<string> GenerationDownloadVideoContentUrlCandidates { get; set; } = new();
}

public sealed class EndpointProfileVideoApiModeOption
{
    public string Mode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
}