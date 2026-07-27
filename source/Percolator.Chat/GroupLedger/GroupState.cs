namespace Percolator.Chat.GroupLedger;

using Percolator.Chat.Messaging.ValueObjects;

/// <summary>
/// Domain model for the mutable state/metadata of a group conversation.
/// </summary>
public sealed class GroupState
{
    public ConversationId ConversationId { get; }
    public int Epoch { get; private set; }
    public string? Name { get; private set; }
    public EncryptedGroupProfileBytes EncryptedProfile { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public GroupState(ConversationId conversationId, int epoch, string? name, EncryptedGroupProfileBytes encryptedProfile, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        ConversationId = conversationId;
        Epoch = epoch;
        Name = name;
        EncryptedProfile = encryptedProfile;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public void ChangeName(string? newName, DateTimeOffset when)
    {
        Name = newName;
        UpdatedAtUtc = when;
    }

    public void IncrementEpoch(DateTimeOffset when)
    {
        Epoch++;
        UpdatedAtUtc = when;
    }
}
