using System;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatedRelayQueueItemViewModel
{
    private static readonly Guid MainNodeSentinelPeerId = new("88880000-0000-0000-0000-000000000000");
    public SimulatedRelayQueueItemViewModel(
        Guid relayHostPeerId,
        RelayQueuedBlobDto model,
        Func<Guid, string> peerNameById,
        Guid? mainIdentityId)
    {
        RelayHostPeerId = relayHostPeerId;
        Model = model;

        AckId = model.AckId;
        EnqueuedUtc = model.EnqueuedUtc;

        DebugType = model.DebugType;
        TypeLabel = ToTypeLabel(model.DebugType);

        RecipientDisplay = ToRecipientDisplay(model.RecipientRoutingKey, peerNameById, mainIdentityId);
        FromToDisplay = $"{peerNameById(relayHostPeerId)} -> {RecipientDisplay}";

        TimestampDisplay = model.EnqueuedUtc.LocalDateTime.ToString("HH:mm:ss");
    }

    public Guid RelayHostPeerId { get; }

    public RelayQueuedBlobDto Model { get; }

    public Guid AckId { get; }

    public DateTimeOffset EnqueuedUtc { get; }

    public string? DebugType { get; }

    public string TypeLabel { get; }

    public string TimestampDisplay { get; }

    public string RecipientDisplay { get; }

    public string FromToDisplay { get; }

    public byte[] RecipientRoutingKey => Model.RecipientRoutingKey;

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

    private static string ToRecipientDisplay(
        byte[] routingKey,
        Func<Guid, string> peerNameById,
        Guid? mainIdentityId)
    {
        if (routingKey is null || routingKey.Length == 0)
        {
            return "?";
        }

        // Guid routing key (simulated peer id or main identity id)
        if (routingKey.Length == 16)
        {
            var id = new Guid(routingKey);
            if ((mainIdentityId.HasValue && id == mainIdentityId.Value) || id == MainNodeSentinelPeerId)
            {
                return "Main Node";
            }
            return peerNameById(id);
        }

        // PublicKeyHash routing key
        var hex = Convert.ToHexString(routingKey);
        return $"PKH:{TruncateHex(hex, 12)}";
    }

    private static string TruncateHex(string hex, int chars)
    {
        if (string.IsNullOrWhiteSpace(hex)) return "";
        if (hex.Length <= chars) return hex;
        return hex[..chars] + "...";
    }
}
