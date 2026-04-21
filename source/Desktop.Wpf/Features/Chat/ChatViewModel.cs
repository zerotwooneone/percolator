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

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatViewModel : ViewModelBase
{
    public object? Messages { get; private set; }
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

    private string? _sessionId;
    private readonly SessionContext _sessionContext;
    private readonly Desktop.Wpf.Features.Chat.State.ChatStateService _chatState;
    private readonly IChatReloadCoordinator _reloadCoordinator;
    private readonly IMediator _mediator;
    private readonly IUiDispatcher _ui;
    private DisposableBag _bag;
    private ISynchronizedView<ChatMessageModel, ChatMessageModel>? _messagesView;
    private INotifyCollectionChangedSynchronizedViewList<ChatMessageModel>? _messagesSyncList;

    public ChatViewModel(
        SessionContext sessionContext,
        Desktop.Wpf.Features.Chat.State.ChatStateService chatState,
        IChatReloadCoordinator reloadCoordinator,
        IMediator mediator,
        IUiDispatcher ui)
    {
        _sessionContext = sessionContext;
        _chatState = chatState;
        _reloadCoordinator = reloadCoordinator;
        _mediator = mediator;
        _ui = ui;

        MessageInput = _sessionContext.Draft;
        CanSend = MessageInput.Select(text => !string.IsNullOrWhiteSpace(text)).ToBindableReactiveProperty(false);
        // Header binds to SessionContext
        Title = _sessionContext.PeerName;
        Initials = _sessionContext.Initials;
        IsOnline = _sessionContext.IsOnline;

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
            
            var sessionGuid = Guid.Parse(_sessionId);
            var messageId = MessageId.NewId();
            var sentTimestamp = DateTimeOffset.UtcNow;
            
            await _mediator.Send(new PostTextMessageCommand(
                ConversationLookupKey.ForDirectSession(sessionGuid),
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

    public void SetSession(string sessionId)
    {
        _sessionId = sessionId;
        _bag = new DisposableBag();

        // 1. Get the raw, unsorted domain list from the state service
        var domainList = _chatState.GetOrAddSessionMessagesList(sessionId);

        // 2. Create a pass-through view and track it
        _messagesView = domainList.CreateView(m => m).AddTo(ref _bag);

        // 3. Bridge the view to the WPF UI thread using Cysharp's native synchronizer
        _messagesSyncList = _messagesView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

        // 4. Wrap it in a WPF CollectionView for native chronological sorting
        var collectionView = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(_messagesSyncList);
        collectionView.CustomSort = new ChatMessageChronologicalComparer();
        
        // Assign directly to the property so WPF can bind to the view
        Messages = collectionView;

        // 5. Force the UI to refresh its sort/filter when background mutations occur
        _chatState.StateMutated
            .ObserveOnCurrentSynchronizationContext()
            .Subscribe(_ => collectionView.Refresh())
            .AddTo(ref _bag);

        // Note: Initial reload is not triggered here because SetSession receives a PeerConnectionKey.Value (PeerId),
        // not a ConversationId. Reload is triggered by ChatStateUpdateHandlers when messages are posted/received.
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

    private sealed class ChatMessageChronologicalComparer : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            
            var msgX = (Desktop.Wpf.Features.Chat.ChatMessageModel)x;
            var msgY = (Desktop.Wpf.Features.Chat.ChatMessageModel)y;
            
            // Ascending: oldest at the top, newest at the bottom
            return msgX.Timestamp.CompareTo(msgY.Timestamp);
        }
    }
}
