using ObservableCollections;
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorChatViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> MessageInput { get; }
    public ReactiveAsyncCommand SendMessageCommand { get; }
    public NotifyCollectionChangedSynchronizedViewList<SimulatorChatMessageModel> Messages { get; }

    private readonly SimulatedPeerModel _model;
    private readonly ISimulatorStateService _state;
    private readonly ISynchronizedView<SimulatedChatMessageSnapshot, SimulatorChatMessageModel> _messagesView;
    private DisposableBag _bag;

    public SimulatorChatViewModel(SimulatedPeerModel model, ISimulatorStateService state, IUiDispatcher ui)
    {
        _model = model;
        _state = state;

        MessageInput = new BindableReactiveProperty<string>(string.Empty).AddTo(ref _bag);

        _messagesView = _model.RecentChatMessages
            .CreateView(m => new SimulatorChatMessageModel(m))
            .AddTo(ref _bag);
        Messages = _messagesView.ToNotifyCollectionChanged(ui.CollectionEventDispatcher);

        SendMessageCommand = new ReactiveAsyncCommand(
            ui,
            async _ =>
        {
            var text = MessageInput.Value;
            if (string.IsNullOrWhiteSpace(text)) return;

            await _state.SendChatMessageToMainAsync(_model.NetworkPeerId, text, CancellationToken.None);
            MessageInput.Value = string.Empty;
        },
            requery: MessageInput.Select(_ => Unit.Default),
            canExecute: _ => !string.IsNullOrWhiteSpace(MessageInput.Value))
            .AddTo(ref _bag);
    }

    protected override void DisposeCore()
    {
        Messages.Dispose(); // MUST dispose ToNotifyCollectionChanged adapter (wpf.readme.md §3)
        _bag.Dispose();
    }
}

public sealed class SimulatorChatMessageModel
{
    public string Content { get; }
    public bool IsFromMain { get; }
    public DateTimeOffset Timestamp { get; }
    public string Direction => IsFromMain ? "← From Main" : "→ To Main";

    public SimulatorChatMessageModel(SimulatedChatMessageSnapshot snapshot)
    {
        Content = snapshot.Content;
        IsFromMain = snapshot.IsFromMain;
        Timestamp = snapshot.ReceivedUtc;
    }
}
