using System;

namespace Desktop.Wpf.Features.Simulator;

public sealed class RelayPeerOption
{
    public RelayPeerOption(Guid? peerId, string displayText)
    {
        PeerId = peerId;
        DisplayText = displayText;
    }

    public Guid? PeerId { get; }

    public string DisplayText { get; }
}
