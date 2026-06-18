using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Apps.Chat;

/// <summary>
/// Service for provisioning new groups on the Relay's encrypted ledger.
/// </summary>
public sealed class RelayGroupProvisioningService : IRelayGroupProvisioningService
{
    private readonly IRelayGroupRepository _relayGroupRepository;

    public RelayGroupProvisioningService(
        IRelayGroupRepository relayGroupRepository)
    {
        _relayGroupRepository = relayGroupRepository;
    }

    public async Task<ProvisionRelayGroupResponse> ProvisionAsync(ProvisionRelayGroupRequest request, CancellationToken ct = default)
    {
        // Validate inputs
        if (request.ConversationId == null || request.ConversationId.Length == 0)
        {
            return new ProvisionRelayGroupResponse { Status = ProvisionRelayGroupResponse.Types.Status.AlreadyExists };
        }

        var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));

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
        var roster = new List<IdentityPublicKeyHash>();
        foreach (var routingToken in request.InitialRoutingTokens)
        {
            roster.Add(IdentityPublicKeyHash.FromBytesOwned(routingToken.ToByteArray()));
        }

        note("need to add roster to ledger?");
        // Save ledger and roster
        await _relayGroupRepository.SaveAsync(ledger, ct).ConfigureAwait(false);

        return new ProvisionRelayGroupResponse { Status = ProvisionRelayGroupResponse.Types.Status.Success };
    }
}
