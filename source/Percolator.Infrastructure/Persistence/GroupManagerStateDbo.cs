using System;

namespace Percolator.Infrastructure.Persistence;

public class GroupManagerStateDbo
{
    public Guid ConversationId { get; set; }
    public byte[] StateBlob { get; set; } = Array.Empty<byte>();
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
