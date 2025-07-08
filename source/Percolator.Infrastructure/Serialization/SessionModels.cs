namespace Percolator.Infrastructure.Serialization;

public class SessionStateModel
{
    public string RootKey { get; set; } = string.Empty;
    public string? SendingChainKey { get; set; }
    public string? ReceivingChainKey { get; set; }
    public ulong SendingCounter { get; set; }
    public ulong ReceivingCounter { get; set; }
    public Dictionary<string, string> SkippedMessageKeys { get; set; } = new();
    public string? TheirIdentityPublicKey { get; set; }
    public string? TheirDhRatchetPublicKey { get; set; }
    public string? DhRatchetPrivateKey { get; set; }
}
