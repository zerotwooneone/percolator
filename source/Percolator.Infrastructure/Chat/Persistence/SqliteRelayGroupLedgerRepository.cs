using Microsoft.EntityFrameworkCore;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.GroupMembership;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat.Persistence;

public sealed class SqliteRelayGroupLedgerRepository : IRelayGroupLedgerRepository
{
    private readonly PercolatorDbContext _db;

    public SqliteRelayGroupLedgerRepository(PercolatorDbContext db) => _db = db;

    public async Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken)
    {
        var dbo = await _db.RelayGroupStates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ConversationId == id.Value, cancellationToken);

        if (dbo == null)
            return null;

        return new RelayGroupLedger(
            new ConversationId(dbo.ConversationId),
            dbo.Epoch,
            RelayGroupPublicParamsBytes.FromBytesOwned(dbo.GroupPublicParams),
            EncryptedGroupProfileBytes.FromBytesOwned(dbo.EncryptedProfile),
            dbo.Version);
    }

    public async Task ProvisionNewGroupAsync(
        ConversationId conversationId,
        RelayGroupPublicParamsBytes publicParams,
        EncryptedGroupProfileBytes encryptedProfile,
        IReadOnlyList<ChatPeerId> memberPeerIds,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var existingGroup = await _db.RelayGroupStates
                .AsNoTracking()
                .FirstOrDefaultAsync(g => g.ConversationId == conversationId.Value, cancellationToken);

            if (existingGroup is not null)
            {
                return; // Already exists, idempotent
            }

            var relayGroupState = new RelayGroupStateDbo
            {
                ConversationId = conversationId.Value,
                GroupPublicParams = publicParams.ToArray(),
                EncryptedProfile = encryptedProfile.ToArray(),
                Epoch = 0,
                Version = 1
            };
            _db.RelayGroupStates.Add(relayGroupState);

            foreach (var peerId in memberPeerIds)
            {
                var blindedRosterEntry = new RelayBlindedRosterDbo
                {
                    ConversationId = conversationId.Value,
                    MemberPeerId = peerId.Value,
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

    public async Task<bool> IsMemberAsync(ConversationId conversationId, ChatPeerId peerId, CancellationToken cancellationToken)
    {
        return await _db.RelayBlindedRosters
            .AsNoTracking()
            .AnyAsync(e => e.ConversationId == conversationId.Value && e.MemberPeerId == peerId.Value, cancellationToken);
    }

    public async Task UpdateGroupStateAsync(
        RelayGroupLedger ledger,
        IReadOnlyList<ChatPeerId> addPeerIds,
        IReadOnlyList<ChatPeerId> removePeerIds,
        CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Update the ledger state
            var dbo = await _db.RelayGroupStates
                .FirstOrDefaultAsync(e => e.ConversationId == ledger.ConversationId.Value, cancellationToken);

            if (dbo is null)
            {
                throw new InvalidOperationException($"Relay group state not found for conversation {ledger.ConversationId.Value}.");
            }

            dbo.Epoch = ledger.CurrentEpoch;
            dbo.EncryptedProfile = ledger.EncryptedProfile.ToArray();
            dbo.Version++;

            // Add new members to the blinded roster
            foreach (var peerId in addPeerIds)
            {
                var existing = await _db.RelayBlindedRosters
                    .AnyAsync(e => e.ConversationId == ledger.ConversationId.Value && e.MemberPeerId == peerId.Value, cancellationToken);

                if (!existing)
                {
                    _db.RelayBlindedRosters.Add(new RelayBlindedRosterDbo
                    {
                        ConversationId = ledger.ConversationId.Value,
                        MemberPeerId = peerId.Value,
                        AddedAtUtc = DateTimeOffset.UtcNow
                    });
                }
            }

            // Remove members from the blinded roster
            foreach (var peerId in removePeerIds)
            {
                var entries = await _db.RelayBlindedRosters
                    .Where(e => e.ConversationId == ledger.ConversationId.Value && e.MemberPeerId == peerId.Value)
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
