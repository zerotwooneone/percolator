using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network.RelayLedger;
using Percolator.Network.ValueObjects;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayGroupLedgerRepository : IRelayGroupLedgerRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupLedgerRepository(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupLedger?> GetByIdAsync(RelayGroupId id, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayGroupLedger(
            new RelayGroupId(dbo.ConversationId),
            new RelayGroupEpoch(dbo.Epoch),
            RelayGroupPublicParamsBytes.FromBytesOwned(dbo.GroupPublicParams),
            RelayProfileBytes.FromBytesOwned(dbo.EncryptedProfile),
            dbo.Version);
    }

    public async Task ProvisionNewGroupAsync(
        RelayGroupId groupId,
        RelayGroupPublicParamsBytes publicParams,
        RelayProfileBytes encryptedProfile,
        IReadOnlyList<byte[]> routingTokens,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var existingGroup = await _db.RelayGroupStates
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.ConversationId == groupId.Value, cancellationToken);

            if (existingGroup is not null)
            {
                return; // Already exists, idempotent
            }

            var relayGroupState = new Percolator.Infrastructure.Network.RelayLedger.RelayGroupStateDbo
            {
                ConversationId = groupId.Value,
                GroupPublicParams = publicParams.ToArray(),
                EncryptedProfile = encryptedProfile.ToArray(),
                Epoch = 0,
                Version = 1
            };
            _db.RelayGroupStates.Add(relayGroupState);

            foreach (var routingToken in routingTokens)
            {
                var blindedRosterEntry = new Percolator.Infrastructure.Network.RelayLedger.RelayBlindedRosterDbo
                {
                    ConversationId = groupId.Value,
                    RoutingToken = routingToken,
                    AddedAtUtc = DateTimeOffset.UtcNow
                };
                _db.RelayBlindedRosters.Add(blindedRosterEntry);
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> IsMemberAsync(RelayGroupId groupId, byte[] routingToken, CancellationToken cancellationToken)
    {
        return await _db.RelayBlindedRosters
            .AsNoTracking()
            .AnyAsync(e => e.ConversationId == groupId.Value && e.RoutingToken == routingToken, cancellationToken);
    }

    public async Task UpdateGroupStateAsync(
        RelayGroupLedger ledger,
        IReadOnlyList<byte[]> addRoutingTokens,
        IReadOnlyList<byte[]> removeRoutingTokens,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Update the ledger state
            var dbo = await _db.RelayGroupStates
                .FirstOrDefaultAsync(e => e.ConversationId == ledger.Id.Value, cancellationToken);

            if (dbo is null)
            {
                throw new InvalidOperationException($"Relay group state not found for group {ledger.Id.Value}.");
            }

            dbo.Epoch = ledger.CurrentEpoch.Value;
            dbo.EncryptedProfile = ledger.EncryptedProfile.ToArray();
            dbo.Version++;

            // Add new members to the blinded roster
            foreach (var routingToken in addRoutingTokens)
            {
                var existing = await _db.RelayBlindedRosters
                    .AnyAsync(e => e.ConversationId == ledger.Id.Value && e.RoutingToken == routingToken, cancellationToken);

                if (!existing)
                {
                    _db.RelayBlindedRosters.Add(new Percolator.Infrastructure.Network.RelayLedger.RelayBlindedRosterDbo
                    {
                        ConversationId = ledger.Id.Value,
                        RoutingToken = routingToken,
                        AddedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }

            // Remove members from the blinded roster
            foreach (var routingToken in removeRoutingTokens)
            {
                var entries = await _db.RelayBlindedRosters
                    .Where(e => e.ConversationId == ledger.Id.Value && e.RoutingToken == routingToken)
                    .ToListAsync(cancellationToken);

                foreach (var entry in entries)
                {
                    _db.RelayBlindedRosters.Remove(entry);
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
