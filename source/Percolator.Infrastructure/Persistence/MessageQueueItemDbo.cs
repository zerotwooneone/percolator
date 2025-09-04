using System;
using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class MessageQueueItemDbo
{
    public Guid Id { get; set; }
    public PeerId RecipientPeerId { get; set; } = null!;
    public byte[] Blob { get; set; } = Array.Empty<byte>();
    public DateTimeOffset EnqueuedAtUtc { get; set; }
}
