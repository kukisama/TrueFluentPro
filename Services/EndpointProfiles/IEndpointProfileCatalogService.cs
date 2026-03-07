using System.Collections.Generic;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public interface IEndpointProfileCatalogService
{
    IReadOnlyList<EndpointProfileDefinition> GetProfiles();
    EndpointProfileDefinition GetProfile(EndpointApiType endpointType);
    EndpointProfileDefinition? FindProfile(string profileId);
    EndpointArchitectureInventory GetArchitectureInventory();
}
