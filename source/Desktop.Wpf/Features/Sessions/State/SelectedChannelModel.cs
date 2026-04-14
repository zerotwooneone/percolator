using Desktop.Wpf.Features.Sessions.Models;
using R3;

namespace Desktop.Wpf.Features.Sessions.State;

public sealed class SelectedChannelModel
{
    public ReactiveProperty<PeerConnectionKey?> SelectedKey { get; } = new(null);
}
