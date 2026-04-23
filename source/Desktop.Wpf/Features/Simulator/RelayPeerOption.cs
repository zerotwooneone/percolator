using Percolator.Network;

namespace Desktop.Wpf.Features.Simulator;

public sealed class RelayPeerOption
{
    public RelayPeerOption(PeerId? peerId, string displayText)
    {
        PeerId = peerId;
        DisplayText = displayText;
    }

    public PeerId? PeerId { get; }

    public string DisplayText { get; }
}
