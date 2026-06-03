using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Network;

public class IdentityNetworkService : IIdentityNetworkService
{
    private readonly INetworkEnvironment _networkEnvironment;
    private readonly ISelfIdentityRepository _identityRepository;
    private readonly IReservedPortQuery _reservedPortQuery;

    public IdentityNetworkService(
        INetworkEnvironment networkEnvironment,
        ISelfIdentityRepository identityRepository,
        IReservedPortQuery reservedPortQuery)
    {
        _networkEnvironment = networkEnvironment;
        _identityRepository = identityRepository;
        _reservedPortQuery = reservedPortQuery;
    }
    
    public async Task ResolvePortContentionAsync(SelfId selfId, CancellationToken ct)
    {
        var identity = await _identityRepository.GetByIdAsync(selfId, ct).ConfigureAwait(false);
        if (identity is null)
        {
            throw new InvalidOperationException($"Identity {selfId} not found");
        }
        var excludedPorts = await _reservedPortQuery.GetReservedPortsAsync(ct).ConfigureAwait(false);
        var newPort = await _networkEnvironment.GetAvailablePortAsync(excludedPorts,ct).ConfigureAwait(false);
        identity.UpdateListeningPort(new ListeningPort(newPort));
        await _identityRepository.SaveAsync(identity, ct).ConfigureAwait(false);
    }
}
