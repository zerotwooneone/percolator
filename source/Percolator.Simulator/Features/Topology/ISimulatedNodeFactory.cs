using System.Threading;
using System.Threading.Tasks;
using Percolator.Simulator.Features.RelayedChat;

namespace Percolator.Simulator.Features.Topology;

internal interface ISimulatedNodeFactory
{
    Task<ISimulatedPersonaHost> CreatePersonaHostAsync(string? displayName = null, CancellationToken ct = default);
    Task<ISimulatedRelayHost> CreateRelayHostAsync(string? displayName = null, CancellationToken ct = default);
}
