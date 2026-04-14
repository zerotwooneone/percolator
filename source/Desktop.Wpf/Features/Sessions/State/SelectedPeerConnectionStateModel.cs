using Desktop.Wpf.Features.Sessions.Models;
using R3;

namespace Desktop.Wpf.Features.Sessions.State;

public sealed class SelectedPeerConnectionStateModel : IDisposable
{
    private DisposableBag _bag;

    public SelectedPeerConnectionStateModel(PeerConnectionKey channelKey)
    {
        ChannelKey = channelKey;
        DraftMessageText = new BindableReactiveProperty<string>(string.Empty).AddTo(ref _bag);
        IsUplinkOpen = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        IsReestablishing = new BindableReactiveProperty<bool>(false).AddTo(ref _bag);
        ReestablishingText = new BindableReactiveProperty<string?>(null).AddTo(ref _bag);
    }

    public PeerConnectionKey ChannelKey { get; }

    public BindableReactiveProperty<string> DraftMessageText { get; }

    public BindableReactiveProperty<bool> IsUplinkOpen { get; }

    public BindableReactiveProperty<bool> IsReestablishing { get; }

    public BindableReactiveProperty<string?> ReestablishingText { get; }

    public void Dispose() => _bag.Dispose();
}
