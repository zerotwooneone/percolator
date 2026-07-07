using Percolator.Identity;

namespace Percolator.Infrastructure.Persistence;

public class SessionDbo
{
    public SelfId SelfIdentityId { get; set; }
    public Guid SessionId { get; set; }
    public PeerId RemotePeerId { get; set; }
    public int ProtocolVersion { get; set; }

    public byte[] RootKey { get; set; } = Array.Empty<byte>();
    public byte[]? SendChainKey { get; set; }
    public ulong SendCounter { get; set; }
    public byte[]? RecvChainKey { get; set; }
    public ulong RecvCounter { get; set; }
    public ulong PrevChainLength { get; set; }
    public byte[]? RemoteRatchetKey { get; set; }
    public byte[]? DhRatchetPrivateKey { get; set; }

    public byte[]? AssociatedData { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUsedAtUtc { get; set; }
}
