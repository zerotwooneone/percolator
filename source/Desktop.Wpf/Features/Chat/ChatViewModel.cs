using System.Windows.Data;
using R3;
using ObservableCollections;
using Desktop.Wpf.Shared.Mvvm;
using Desktop.Wpf.Features.Sessions;
using MediatR;
using Percolator.Chat.App;
using Percolator.Chat.App.Commands;
using Percolator.Chat.Primitives;
using Percolator.Chat.ValueObjects;
using Percolator.Network;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatViewModel : ViewModelBase
{
    public NotifyCollectionChangedSynchronizedViewList<ChatMessageViewModel> Messages { get; private set; }
    public BindableReactiveProperty<string> MessageInput { get; }
    public BindableReactiveProperty<bool> CanSend { get; }
    public BindableReactiveProperty<string> Title { get; }
    public BindableReactiveProperty<string> Initials { get; }
    public BindableReactiveProperty<bool> IsOnline { get; }
    public AsyncRelayCommand SendCommand { get; }
    public BindableReactiveProperty<bool> IsNetworkOpen { get; }
    public BindableReactiveProperty<bool> IsRelayed { get; }
    public BindableReactiveProperty<string> RouteIcon { get; }
    public BindableReactiveProperty<string> RouteText { get; }
    public AsyncRelayCommand ToggleNetworkCommand { get; }
    public AsyncRelayCommand CloseNetworkCommand { get; }
    public BindableReactiveProperty<bool> AutoDiscoveryEnabled { get; }

    private DirectSessionId? _sessionId;
    private readonly SessionContext _sessionContext;
    private readonly Desktop.Wpf.Features.Chat.State.ChatStateService _chatState;
    private readonly IMediator _mediator;
    private readonly IUiDispatcher _ui;
    private DisposableBag _bag;
    private ISynchronizedView<ChatMessageModel, ChatMessageViewModel>? _messagesView;

    public ChatViewModel(
        SessionContext sessionContext,
        Desktop.Wpf.Features.Chat.State.ChatStateService chatState,
        IMediator mediator,
        IUiDispatcher ui)
    {
        _sessionContext = sessionContext;
        _chatState = chatState;
        _mediator = mediator;
        _ui = ui;

        // Convert SessionContext ReactiveProperty to BindableReactiveProperty for UI binding
        MessageInput = _sessionContext.Draft.ToBindableReactiveProperty(string.Empty);
        CanSend = MessageInput.Select(text => !string.IsNullOrWhiteSpace(text)).ToBindableReactiveProperty(false);
        // Header binds to SessionContext
        Title = _sessionContext.PeerName.ToBindableReactiveProperty("");
        Initials = _sessionContext.Initials.ToBindableReactiveProperty("?");
        IsOnline = _sessionContext.IsOnline.ToBindableReactiveProperty(false);

        IsNetworkOpen = new BindableReactiveProperty<bool>(false);
        IsRelayed = new BindableReactiveProperty<bool>(false); // seed: direct
        // Route presentation derived from IsRelayed
        RouteIcon = IsRelayed
            .Select(relay => relay ? "\uF50F" : "\uF0B45")
            .ToBindableReactiveProperty("\uF0B45");
        RouteText = IsRelayed
            .Select(relay => relay ? "Relayed Route" : "Direct Route")
            .ToBindableReactiveProperty("Direct Route");
        AutoDiscoveryEnabled = new BindableReactiveProperty<bool>(false);

        SendCommand = new AsyncRelayCommand(async _ =>
        {
            if (_sessionId is null) return;
            var text = MessageInput.Value;
            if (string.IsNullOrWhiteSpace(text)) return;

            var messageId = MessageId.NewId();
            var sentTimestamp = DateTimeOffset.UtcNow;

            await _mediator.Send(new PostTextMessageCommand(
                ConversationLookupKey.ForDirectSession(_sessionId.Value.Value),
                messageId,
                text,
                sentTimestamp));

            MessageInput.Value = string.Empty;
        }, _ => CanSend.Value);

        CanSend.Subscribe(_ => SendCommand.RaiseCanExecuteChanged());

        ToggleNetworkCommand = new AsyncRelayCommand(async _ =>
        {
            IsNetworkOpen.Value = !IsNetworkOpen.Value;
            await Task.CompletedTask;
        });

        CloseNetworkCommand = new AsyncRelayCommand(async _ =>
        {
            IsNetworkOpen.Value = false;
            await Task.CompletedTask;
        });
    }

    public void SetSession(DirectSessionId sessionId)
    {
        _sessionId = sessionId;
        _bag = new DisposableBag();

        // 1. Get the raw, unsorted domain list from the state service
        var domainList = _chatState.GetOrAddSessionMessagesList(sessionId);

        // 2. Create a view that projects ChatMessageModel -> ChatMessageViewModel (established pattern from SimulatorPeersTabViewModel)
        _messagesView = domainList.CreateView(m => new ChatMessageViewModel(m, _ui)).AddTo(ref _bag);

        // 3. Bridge the view to the WPF UI thread using Cysharp's native synchronizer
        Messages = _messagesView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        // 4. Do NOT subscribe to a "state mutated" signal just to refresh UI.
        // 5. Do NOT sort in the ViewModel — per wpf.readme.md §1, sorting is a XAML concern.
    }

    protected override void DisposeCore()
    {
        _bag.Dispose();
        Disposable.Dispose(MessageInput, CanSend, Title, Initials, IsOnline, IsNetworkOpen, IsRelayed, AutoDiscoveryEnabled, RouteIcon, RouteText);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
