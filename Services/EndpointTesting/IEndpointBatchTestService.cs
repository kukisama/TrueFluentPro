using System;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services.EndpointTesting;

public interface IEndpointBatchTestService
{
    Task<EndpointBatchTestReport> TestSelectedEndpointAsync(
        AzureSpeechConfig config,
        AiEndpoint endpoint,
        IProgress<EndpointBatchTestProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default);
}
