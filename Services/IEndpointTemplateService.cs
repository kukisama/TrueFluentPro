using System.Collections.Generic;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services;

public interface IEndpointTemplateService
{
    IReadOnlyList<EndpointTemplateDefinition> GetTemplates();
    EndpointTemplateDefinition GetTemplate(EndpointApiType type);
    EndpointTemplateDefinition GetTemplate(AiEndpoint endpoint);
    void ApplyTemplate(AiEndpoint endpoint, EndpointApiType type);
    string BuildBehaviorSummary(AiEndpoint endpoint);
    EndpointInspectionDetails BuildInspectionDetailsModel(AiEndpoint endpoint);
    string BuildInspectionDetails(AiEndpoint endpoint);
}