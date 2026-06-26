namespace Percolator.Chat.GroupLedger;

/// <summary>
/// Domain model for the mutable state/metadata of a group conversation.
/// </summary>
public sealed class GroupState
{
    public Messaging.ValueObjects.ConversationId ConversationId { get; }
    public int Epoch { get; private set; }
    public string? Name { get; private set; }
    public RelayGroupPublicParamsBytes PublicParams { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public GroupState(Messaging.ValueObjects.ConversationId conversationId, int epoch, string? name, RelayGroupPublicParamsBytes publicParams, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        ConversationId = conversationId;
        Epoch = epoch;
        Name = name;
        PublicParams = publicParams;
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
