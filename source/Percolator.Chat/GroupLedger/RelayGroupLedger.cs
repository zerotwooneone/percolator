using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Chat.GroupMembership;

namespace Percolator.Chat.GroupLedger;

public sealed class RelayGroupLedger
{
    public ConversationId ConversationId { get; private set; }
    public uint CurrentEpoch { get; private set; }
    public RelayGroupPublicParamsBytes GroupPublicParams { get; private set; }
    public int Version { get; private set; }

    public RelayGroupLedger(
        ConversationId conversationId,
        uint currentEpoch,
        RelayGroupPublicParamsBytes groupPublicParams,
        int version)
    {
        ConversationId = conversationId;
        CurrentEpoch = currentEpoch;
        GroupPublicParams = groupPublicParams;
        Version = version;
    }

    public void AdvanceEpoch(uint requestedEpoch)
    {
        if (requestedEpoch <= CurrentEpoch)
            throw new EpochConflictDomainException($"Requested epoch {requestedEpoch} is stale.");
        CurrentEpoch = requestedEpoch;
    }
}
