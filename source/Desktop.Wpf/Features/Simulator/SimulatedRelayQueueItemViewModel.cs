using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedRelayQueueItemViewModel
{
    public SimulatedRelayQueueItemViewModel(
        NetworkPeerId relayHostNetworkPeerId,
        Guid ackId,
        DateTimeOffset enqueuedUtc,
        string? debugType,
        byte[]? targetIdentityPublicKeyHash,
        byte[] opaqueBytes,
        Func<NetworkPeerId, string> peerNameById)
    {
        RelayHostNetworkPeerId = relayHostNetworkPeerId;

        AckId = ackId;
        EnqueuedUtc = enqueuedUtc;
        DebugType = debugType;
        TargetIdentityPublicKeyHash = targetIdentityPublicKeyHash;
        OpaqueBytes = opaqueBytes;

        TypeLabel = ToTypeLabel(debugType);

        RecipientDisplay = ToRecipientDisplay(targetIdentityPublicKeyHash);
        FromToDisplay = $"{peerNameById(relayHostNetworkPeerId)} -> {RecipientDisplay}";

        TimestampDisplay = enqueuedUtc.LocalDateTime.ToString("HH:mm:ss");
    }

    public NetworkPeerId RelayHostNetworkPeerId { get; }

    public Guid AckId { get; }

    public DateTimeOffset EnqueuedUtc { get; }

    public string? DebugType { get; }

    public byte[]? TargetIdentityPublicKeyHash { get; }

    public byte[] OpaqueBytes { get; }

    public string TypeLabel { get; }

    public string TimestampDisplay { get; }

    public string RecipientDisplay { get; }

    public string FromToDisplay { get; }

    public static string ToTypeLabel(string? debugType)
    {
        if (string.IsNullOrWhiteSpace(debugType)) return "[OPAQUE]";

        return debugType switch
        {
            nameof(Percolator.Contracts.EstablishDirectSessionRequest) => "[REVERSE_INVITE]",
            nameof(Percolator.Contracts.InviteHandshakeResponse) => "[REVERSE_ACCEPT]",
            nameof(Percolator.Contracts.EstablishSessionRequest) => "[X3DH_INIT]",
            nameof(Percolator.Contracts.EstablishSessionResponse) => "[X3DH_ACCEPT]",
            nameof(Percolator.Contracts.DeliverOpaqueMessageRequest) => "[MESSAGE]",
            _ => $"[{debugType}]"
        };
    }

    private static string ToRecipientDisplay(byte[]? targetPkh)
    {
        if (targetPkh is null || targetPkh.Length == 0)
        {
            return "Main Node";
        }

        var hex = Convert.ToHexString(targetPkh);
        return $"PKH:{TruncateHex(hex, 12)}";
    }

    private static string TruncateHex(string hex, int chars)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "";
        if (hex.Length <= chars) return hex;
        return hex[..chars] + "...";
    }
}
