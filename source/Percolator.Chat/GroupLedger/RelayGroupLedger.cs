using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat.GroupLedger;

public sealed class RelayGroupLedger
{
    public ConversationId ConversationId { get; private set; }
    public uint CurrentEpoch { get; private set; }
    public RelayGroupPublicParamsBytes GroupPublicParams { get; private set; }
    public EncryptedGroupProfileBytes EncryptedProfile { get; private set; }
    public int Version { get; private set; }

    public RelayGroupLedger(
        ConversationId conversationId,
        uint currentEpoch,
        RelayGroupPublicParamsBytes groupPublicParams,
        EncryptedGroupProfileBytes encryptedProfile,
        int version)
    {
        ConversationId = conversationId;
        CurrentEpoch = currentEpoch;
        GroupPublicParams = groupPublicParams;
        EncryptedProfile = encryptedProfile;
        Version = version;
    }

    /// <summary>
    /// Evaluates whether a chat message can be accepted based on epoch concurrency.
    /// Chat messages do not advance the epoch; they must match the current epoch exactly.
    /// </summary>
    public bool CanAcceptChatMessage(uint requestedEpoch)
    {
        return requestedEpoch == CurrentEpoch;
    }

    /// <summary>
    /// Attempts to apply a structural mutation to the group ledger.
    /// This validates the base epoch and, if valid, advances the epoch and updates the encrypted profile.
    /// </summary>
    public bool TryApplyMutation(uint baseEpoch, EncryptedGroupProfileBytes newProfile)
    {
        if (baseEpoch != CurrentEpoch)
        {
            return false;
        }

        CurrentEpoch++;
        EncryptedProfile = newProfile;
        return true;
    }
}
