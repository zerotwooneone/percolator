using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Infrastructure.Persistence;

public class MessageQueueItemDbo
{
    public Guid Id { get; set; }
    // Host-assigned id used for relay acknowledgments. Required, unique.
    public Guid AckId { get; set; }
    public Pkh RecipientPkh { get; set; } = null!;
    public byte[] Blob { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
}
