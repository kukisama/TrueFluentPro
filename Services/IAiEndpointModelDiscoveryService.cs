using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TrueFluentPro.Models;

namespace TrueFluentPro.Services
{
    public interface IAiEndpointModelDiscoveryService
    {
        Task<AiEndpointModelDiscoveryResult> DiscoverModelsAsync(AiEndpoint endpoint, CancellationToken cancellationToken = default);
    }

    public sealed class AiEndpointModelDiscoveryResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<string> ModelIds { get; init; } = new List<string>();
    }
}
