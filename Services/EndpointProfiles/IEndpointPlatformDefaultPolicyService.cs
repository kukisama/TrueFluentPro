using System.Collections.Generic;
using TrueFluentPro.Models;
using TrueFluentPro.Models.EndpointProfiles;

namespace TrueFluentPro.Services.EndpointProfiles;

public interface IEndpointPlatformDefaultPolicyService
{
    EndpointPlatformDefaultCatalog GetCatalog();
    IReadOnlyList<EndpointPlatformDefaultPolicy> GetPolicies();
    EndpointPlatformDefaultPolicy GetPolicy(EndpointApiType endpointType);
}