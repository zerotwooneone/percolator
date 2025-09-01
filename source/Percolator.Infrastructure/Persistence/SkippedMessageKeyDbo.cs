using System;

namespace Percolator.Infrastructure.Persistence;

public class SkippedMessageKeyDbo
{
    public int Id { get; set; }
    public Guid SessionId { get; set; }
    public int SelfIdentityId { get; set; }
    public byte[] RatchetKey { get; set; } = Array.Empty<byte>();
    public ulong MessageNumber { get; set; }

    public byte[] MessageKey { get; set; } = Array.Empty<byte>();

    public DoubleRatchetSessionDbo Session { get; set; } = null!;
}
