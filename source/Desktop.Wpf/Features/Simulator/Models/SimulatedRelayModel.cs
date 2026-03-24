using ObservableCollections;
using R3;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record OutboundRelayMessage(
    Guid AckId,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed record InboundRelayMessage(
    Guid AckId,
    byte[] TargetPkh,
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);

public sealed class SimulatedRelayModel : IDisposable
{
    private readonly ObservableDictionary<Guid, OutboundRelayMessage> _upstreamToMain = new();
    private readonly ObservableDictionary<Guid, InboundRelayMessage> _downstreamToPeers = new();

    public SimulatedRelayModel(Guid relayHostPeerId)
    {
        RelayHostPeerId = relayHostPeerId;
        UpstreamToMain = _upstreamToMain;
        DownstreamToPeers = _downstreamToPeers;
    }

    public Guid RelayHostPeerId { get; }

    public IReadOnlyObservableDictionary<Guid, OutboundRelayMessage> UpstreamToMain { get; }

    public IReadOnlyObservableDictionary<Guid, InboundRelayMessage> DownstreamToPeers { get; }

    public void EnqueueForMain(OutboundRelayMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));

        _upstreamToMain[message.AckId] = message;
    }

    public void EnqueueForPeer(InboundRelayMessage message)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (message.TargetPkh is null) throw new ArgumentNullException(nameof(message.TargetPkh));
        if (message.TargetPkh.Length == 0) throw new ArgumentException("TargetPkh must be non-empty", nameof(message));

        _downstreamToPeers[message.AckId] = message;
    }

    public bool RemoveMessage(Guid ackId)
    {
        var removedMain = _upstreamToMain.Remove(ackId);
        var removedPeer = _downstreamToPeers.Remove(ackId);
        return removedMain || removedPeer;
    }

    public void Dispose()
    {
    }
}
