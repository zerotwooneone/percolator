namespace Percolator.Cryptography.GroupLedger;

/// <summary>
/// Aggregate representing a skipped message key for out-of-order delivery.
/// Stores intermediate symmetric keys for messages that were skipped in the sequence.
/// </summary>
public sealed class SkippedMessageKey
{
    public GroupId GroupId { get; }
    public uint KeyId { get; }
    public int MessageIndex { get; }
    public byte[] MessageKey { get; }

    public SkippedMessageKey(
        GroupId groupId,
        uint keyId,
        int messageIndex,
        byte[] messageKey)
    {
        GroupId = groupId;
        KeyId = keyId;
        MessageIndex = messageIndex;
        MessageKey = messageKey;
    }
}
