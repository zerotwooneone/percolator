using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Simulator.Features.RelayedChat;

internal sealed class SimulatorRelayQueryService : ISimulatorRelayQueryService
{
    private readonly PercolatorDbContext _dbContext;

    public SimulatorRelayQueryService(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<int> GetHostedOpkCountAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        var opks = await GetHostedOneTimePreKeysAsync(ownerId, deviceId, ct);
        return opks.Count;
    }

    public async Task<bool> HasActivePreKeyAsync(
        PublicIdentityId ownerId,
        CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        return await _dbContext.RelayPreKeys
            .AsNoTracking()
            .AnyAsync(k => k.OwnerIdentityId == ownerGuid, ct);
    }

    public async Task<IReadOnlyList<SimulatorHostedPreKeyReadModel>> GetHostedOneTimePreKeysAsync(
        PublicIdentityId ownerId,
        DeviceId deviceId,
        CancellationToken ct = default)
    {
        var ownerGuid = ownerId.Value;
        var entity = await _dbContext.RelayPreKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.OwnerIdentityId == ownerGuid && k.DeviceId == deviceId.Value, ct);

        if (entity == null || string.IsNullOrEmpty(entity.OneTimePreKeysJson))
        {
            return Array.Empty<SimulatorHostedPreKeyReadModel>();
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<OpkDto>>(entity.OneTimePreKeysJson);
            if (dtos == null) return Array.Empty<SimulatorHostedPreKeyReadModel>();

            return dtos.Select(d => new SimulatorHostedPreKeyReadModel(d.KeyId, Convert.FromHexString(d.KeyHex))).ToList();
        }
        catch
        {
            return Array.Empty<SimulatorHostedPreKeyReadModel>();
        }
    }

    private sealed class OpkDto
    {
        public uint KeyId { get; set; }
        public string KeyHex { get; set; } = string.Empty;
    }
}
