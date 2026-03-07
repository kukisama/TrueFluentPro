using System.Collections.Generic;

namespace TrueFluentPro.Models.EndpointProfiles;

public sealed class EndpointArchitectureInventory
{
    public string Description { get; set; } = "";
    public List<EndpointArchitectureOption> Options { get; set; } = new();
    public List<EndpointArchitectureHotspot> Hotspots { get; set; } = new();
}

public sealed class EndpointArchitectureOption
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool PersistedToUserConfig { get; set; }
    public bool DeveloperManaged { get; set; }
    public List<string> CurrentFields { get; set; } = new();
    public List<string> RelatedProfiles { get; set; } = new();
}

public sealed class EndpointArchitectureHotspot
{
    public string FilePath { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> OptionKeys { get; set; } = new();
}
