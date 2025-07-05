namespace Percolator.Infrastructure.Serialization;

public class SessionStateModel
{
    public byte[] RootKey { get; set; } = Array.Empty<byte>();
    public byte[]? SendingChainKey { get; set; }
    public byte[]? ReceivingChainKey { get; set; }
    public ulong SendingCounter { get; set; }
    public ulong ReceivingCounter { get; set; }
    public Dictionary<ulong, byte[]> SkippedMessageKeys { get; set; } = new();
    public byte[]? TheirIdentityPublicKey { get; set; }
    public byte[]? TheirDhRatchetPublicKey { get; set; }
    public byte[]? DhRatchetPrivateKey { get; set; }
}
