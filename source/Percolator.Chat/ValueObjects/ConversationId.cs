using System.Text.Json.Serialization;

namespace Percolator.Chat.ValueObjects;

public readonly record struct ConversationId
{
    public Guid Value { get; }

    [JsonConstructor]
    public ConversationId(Guid Value)
    {
        if (Value == Guid.Empty)
            throw new ArgumentException("Conversation ID cannot be empty.", nameof(Value));
        this.Value = Value;
    }

    public static ConversationId NewId() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
