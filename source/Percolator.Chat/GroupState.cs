using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

/// <summary>
/// Domain model for the mutable state/metadata of a group conversation.
/// </summary>
public sealed class GroupState
{
    public ConversationId ConversationId { get; }
    public int Epoch { get; private set; }
    public string? Name { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public GroupState(ConversationId conversationId, int epoch, string? name, DateTimeOffset createdAtUtc, DateTimeOffset updatedAtUtc)
    {
        ConversationId = conversationId;
        Epoch = epoch;
        Name = name;
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
