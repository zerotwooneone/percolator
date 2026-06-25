using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Application.Network;

namespace Desktop.Wpf.Features.Self;

public sealed class StartupIdentityService : IStartupIdentityService
{
    private readonly ISelfIdentityRepository _repo;
    private readonly IClock _clock;
    private readonly INetworkEnvironment _networkEnvironment;
    private readonly IReservedPortQuery _reservedPortQuery;

    public StartupIdentityService(
        ISelfIdentityRepository repo, 
        IClock clock, 
        INetworkEnvironment networkEnvironment,
        IReservedPortQuery reservedPortQuery)
    {
        _repo = repo;
        _clock = clock;
        _networkEnvironment = networkEnvironment;
        _reservedPortQuery = reservedPortQuery;
    }

    public async Task<SelfIdentity> ResolveOrCreateAsync(CancellationToken ct = default)
    {
        var existing = await _repo.GetMostRecentAsync(ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = _clock.UtcNow;

        var excludedPorts = await _reservedPortQuery.GetReservedPortsAsync(ct).ConfigureAwait(false);
        var port = await _networkEnvironment.GetAvailablePortAsync(excludedPorts,ct).ConfigureAwait(false);
        var created = new SelfIdentity(new SelfId(0), PublicIdentityId.NewId(), new ListeningPort(port), new DeviceId(1), now);
        created.TouchLastUsed(now);
        var newId =await _repo.CreateAsync(created, ct).ConfigureAwait(false);
        return await _repo.GetByIdAsync(newId, ct).ConfigureAwait(false)!;
    }
}
