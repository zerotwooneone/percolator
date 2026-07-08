using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Percolator.Application.Network;

public sealed record OutboundWireMessage(
    Percolator.Network.NetworkPeerId DestinationNetworkPeerId,
    string SendPath,
    string MessageType,
    string? RequestCorrelationId,
    byte[] PayloadBytes,
    int PayloadLength);

public interface IOutboundMessageWireTap
{
    bool Enabled { get; }
    SimulatorOutboundMode Mode { get; }
    void Tap(OutboundWireMessage message);
    IReadOnlyList<OutboundWireMessage> Snapshot();
}

public sealed class OutboundMessageWireTap : IOutboundMessageWireTap
{
    private readonly IOptions<SimulatorWireTapOptions> _options;
    private readonly ConcurrentQueue<OutboundWireMessage> _messages = new();

    public OutboundMessageWireTap(IOptions<SimulatorWireTapOptions> options)
    {
        _options = options;
    }

    public bool Enabled => _options.Value.Enabled;
    public SimulatorOutboundMode Mode => _options.Value.Mode;

    public void Tap(OutboundWireMessage message)
    {
        if (!Enabled)
        {
            return;
        }

        _messages.Enqueue(message);
    }

    public IReadOnlyList<OutboundWireMessage> Snapshot()
    {
        return _messages.ToArray();
    }
}
