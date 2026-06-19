using ObservableCollections;
using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Simulator.Models;

public abstract record RelayMessage(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed record OutboundRelayMessage(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType)
    : RelayMessage(AckId, OpaqueBytes, EnqueuedUtc, DebugType);

public sealed record InboundRelayMessage(
    Guid AckId,
    IdentityPublicKeyHash TargetPkh,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType)
    : RelayMessage(AckId, OpaqueBytes, EnqueuedUtc, DebugType);

public sealed class SimulatedRelayModel : IDisposable
{
    public SimulatedRelayModel(Percolator.Network.PeerId relayHostPeerId)
    {
        RelayHostPeerId = relayHostPeerId;
    }

    public Percolator.Network.PeerId RelayHostPeerId { get; }

    public ReactiveProperty<bool> AutoDeliverEnabled { get; } = new(false);

    public ObservableDictionary<Guid, RelayMessage> MessageQueue { get; } = new();

    public void EnqueueMessage(RelayMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        // Type invariant enforced by IdentityPublicKeyHash - no length check needed
        MessageQueue[message.AckId] = message;
    }

    public bool RemoveMessage(Guid ackId)
    {
        return MessageQueue.Remove(ackId);
    }

    public RelayStateSnapshot Freeze()
    {
        var upstream = MessageQueue
            .Select(kvp => kvp.Value)
            .OfType<OutboundRelayMessage>()
            .OrderBy(x => x.EnqueuedUtc)
            .Select(x => new OutboundRelayMessageSnapshot(
                AckId: x.AckId,
                OpaqueBytes: x.OpaqueBytes.ToArray(),
                EnqueuedUtc: x.EnqueuedUtc,
                DebugType: x.DebugType))
            .ToList();

        var downstream = MessageQueue
            .Select(kvp => kvp.Value)
            .OfType<InboundRelayMessage>()
            .OrderBy(x => x.EnqueuedUtc)
            .Select(x => new InboundRelayMessageSnapshot(
                AckId: x.AckId,
                TargetIdentityPublicKeyHash: x.TargetPkh,
                OpaqueBytes: x.OpaqueBytes.ToArray(),
                EnqueuedUtc: x.EnqueuedUtc,
                DebugType: x.DebugType))
            .ToList();

        return new RelayStateSnapshot(
            RelayHostPeerId: RelayHostPeerId,
            UpstreamToMain: upstream,
            DownstreamToPeers: downstream);
    }

    public void Dispose()
    {
    }
}
