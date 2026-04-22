using Desktop.Wpf.Features.Sessions.Models;
using R3;

namespace Desktop.Wpf.Features.Sessions.State;

public sealed class SelectedPeerConnectionStateModel : IDisposable
{
    private DisposableBag _bag;

    public SelectedPeerConnectionStateModel(PeerConnectionKey channelKey)
    {
        ChannelKey = channelKey;
        IsUplinkOpen = new ReactiveProperty<bool>(false).AddTo(ref _bag);
        IsReestablishing = new ReactiveProperty<bool>(false).AddTo(ref _bag);
        ReestablishingText = new ReactiveProperty<string?>(null).AddTo(ref _bag);
    }

    public PeerConnectionKey ChannelKey { get; }
    public ReactiveProperty<bool> IsUplinkOpen { get; }
    public ReactiveProperty<bool> IsReestablishing { get; }
    public ReactiveProperty<string?> ReestablishingText { get; }

    public void Dispose() => _bag.Dispose();
}
