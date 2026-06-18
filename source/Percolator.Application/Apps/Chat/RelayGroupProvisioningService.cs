using Microsoft.EntityFrameworkCore;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for provisioning new groups on the Relay's encrypted ledger.
/// </summary>
public sealed class RelayGroupProvisioningService : IRelayGroupProvisioningService
{
    private readonly IRelayGroupRepository _relayGroupRepository;
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public RelayGroupProvisioningService(
        IRelayGroupRepository relayGroupRepository,
        IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _relayGroupRepository = relayGroupRepository;
        _dbFactory = dbFactory;
    }

    public async Task<ProvisionRelayGroupResponse> ProvisionAsync(ProvisionRelayGroupRequest request, CancellationToken ct = default)
    {
        // Validate inputs
        if (request.ConversationId == null || request.ConversationId.Length == 0)
        {
            return new ProvisionRelayGroupResponse { Status = ProvisionRelayGroupResponse.Types.Status.AlreadyExists };
        }

        var conversationId = ConversationId.FromBytesOwned(request.ConversationId.ToByteArray());

        // Check if ledger already exists
        var existingLedger = await _relayGroupRepository.GetLedgerAsync(conversationId, ct);
        if (existingLedger != null)
        {
            return new ProvisionRelayGroupResponse { Status = ProvisionRelayGroupResponse.Types.Status.AlreadyExists };
        }

        // Create new ledger
        var groupPublicParams = ZkGroupPublicParamsBytes.FromBytesOwned(request.GroupPublicParams.ToByteArray());
        var ledger = new RelayGroupLedger(conversationId, 0, groupPublicParams);

        // Map PKHs to blinded roster
        using var db = _dbFactory.CreateDbContext();
        foreach (var routingToken in request.InitialRoutingTokens)
        {
            var rosterEntry = new RelayBlindedRosterDbo
            {
                ConversationId = conversationId.Value,
                DestinationPkhBytes = routingToken.ToByteArray()
            };
            db.RelayBlindedRosters.Add(rosterEntry);
        }

        // Save ledger and roster
        await _relayGroupRepository.SaveAsync(ledger, ct);
        await db.SaveChangesAsync(ct);

        return new ProvisionRelayGroupResponse { Status = ProvisionRelayGroupResponse.Types.Status.Success };
    }
}
