using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatorRatchetQueryService : ISimulatorRatchetQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SimulatorRatchetQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<bool> HasActiveSessionAsync(
        PublicIdentityId ownerId,
        PublicIdentityId remotePeerId,
        DeviceId remoteDeviceId,
        CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var remoteGuid = remotePeerId.Value;
        var remoteDevice = remoteDeviceId.Value;

        return await _dbContext.VaultRatchetSessions
            .AsNoTracking()
            .AnyAsync(s => s.OwnerIdentityId == ownerGuid &&
                           s.RemotePeerId == remoteGuid &&
                           s.RemoteDeviceId == remoteDevice, ct);
    }
}
