# Bi-Directional Chat Plan (Main -> Simulator)

**Goal:** Secure bi-directional chat messaging between the Main Window and Simulated Peers over established Direct and Relayed sessions.

## Architecture Guidelines Checklist

| Guideline | Source | Rule                                                                                                                                                                                |
|---|---|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Thread-agnostic services | `r3.readme.md` | State services must not inject `IUiDispatcher`. Mutations under `SemaphoreSlim`/`lock`.                                                                                             |
| ViewModel UI bridging | `wpf.readme.md` | ViewModels own the dispatcher. Collections via `CreateView` to `ToNotifyCollectionChanged(ui.CollectionEventDispatcher)`. Properties via `ObserveOnCurrentSynchronizationContext()`. |
| Domain snapshotting | `r3.readme.md` | `.Freeze()` under lock produces pure immutable records for background I/O.                                                                                                          |
| No ViewModel sorting | `wpf.readme.md` | Sorting in XAML via `CollectionViewSource.GetDefaultView(...)` + `CustomSort`.                                                                                                      |
| Robust tests | `unit-testing.md` | AAA pattern, black-box, no internal-state assertions. Mock external deps only.                                                                                                      |
| Domain isolation | Architecture | Domain projects never reference each other or Application. Cross-domain goes via Application.                                                                                       |
| Contracts project | Architecture | Only protobuf defs. No C# interfaces.                                                                                                                                               |

## Pre-existing Concern: Potential Double-Send

`PostTextMessageHandler` (Application layer) currently sends `ChatEnvelope` directly to all participants (lines 69-76) AND publishes `TextMessagePostedEvent`. `TextMessagePostedHandler` catches that event and dispatches `DispatchTextMessageCommand`, which sends again via `IRemoteEnvelopeSender` (with PKH resolution for relay fallback).

Treat this as a **bug** (duplicate send risk). The preferred fix (when you address it) is:

- Keep the `TextMessagePostedEvent` to `DispatchTextMessageCommand` pipeline as the **only** outbound network send.
- Remove the direct `IRemoteEnvelopeSender` send loop from `PostTextMessageHandler`.

This plan intentionally scopes that fix out; the bi-directional plan remains correct even if the duplicate-send is resolved later.

## Identity & Author Attribution Invariants (verifiable in codebase)

- Peers never exchange `PeerId` values. `PeerId` is a local-only identifier.
- The only cross-peer author identifier is the author's identity signing public key (`SPKI`) or its hash (`PKH = SHA256(SPKI)`).
- For group chat, the author is identified by `TextMessage.AuthorIdentityKey` (SPKI). This is already stamped on outbound messages by `PostTextMessageHandler`.

**Inbound author resolution algorithm (group / relayed / any message that includes SPKI):**

1) Read `authorSpki = chatEnvelope.TextMessage.AuthorIdentityKey`.
2) Compute `authorPkh = SHA256(authorSpki)`.
3) Resolve local peer id via `IPeerPublicSigningKeyStore.GetPeerIdByPublicKeyHashAsync(authorPkh)`.
    - If null: fail fast (throw) in the Application layer. The sender must be introduced/known (e.g., by `SetPeerNameByPublicKeyCommand`) before we can attribute.
4) Convert to Chat domain participant id via `new ParticipantId(peerId.Value)`.

**Layering rule:** Chat domain stays isolated from Identity types. Implement the lookup via an Application-layer adapter over `IPeerPublicSigningKeyStore` (Chat-side boundary is `IPkhPeerResolver`).

**Important type constraint:** In this codebase, `ParticipantId` is a `Guid` wrapper. Any PKHa to peer lookup used for chat author attribution must return a `Guid`-backed identifier (not an `int`).
 
---

## Chunk A

### Code Review Findings

**Domain Types (prefer these over raw `Guid` / `string`):**
- `DirectSessionId` -- `readonly record struct(Guid Value)` in `Percolator.Network`
- `ConversationId` -- `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `MessageId` -- `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `ParticipantId` -- `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `PeerId` -- `record(Guid Value)` in `Percolator.Network`
- `SelfId` -- `readonly record struct(int Value)` in `Percolator.Identity`

**Key Identity Access:**
- `ActiveIdentityContext.Identity.SelfIdentityId` → `SelfId` (wraps `int`). Use `.Value` for raw int.
- `ISelfParticipantIdProvider.Get()` → `ParticipantId` (wraps `Guid`). Different from `SelfId`.

**What's Already Implemented:**
- `IConversationResolver.ResolveAsync(ConversationLookupKey, CancellationToken)` → `ConversationResolution(Conversation, int SelfIdentityId)` -- the canonical way to turn a lookup key into a conversation
- `ConversationLookupKey.ForDirectSession(Guid)` -- factory for direct session lookup
- `ChatReloadCoordinator` -- Singleton, uses R3 `Subject`→`Debounce`→`SelectAwait` pipeline (missing `TimeProvider` injection)
- `PeerConnectionReloadCoordinator` -- reference pattern: injects `TimeProvider`, passes to `Debounce(..., timeProvider)`
- `TimeProvider.System` registered as Singleton in `App.xaml.cs`
- `ChatStateService` -- Singleton, owns `ObservableList<ChatMessageModel>` collections behind a `_stateGate` lock
- `SimulatedPeerModel.AddChatMessage` enforces max 50 messages
- `SendChatMessageToMainAsync` exists in `ISimulatorStateService`
- Peer cards rendered inline in `SimulatorPeersTabView.xaml` DataTemplate -- there is **no** `SimulatedPeerCardView.xaml`
- All three Chat event publish sites (`PostTextMessageHandler`, `ReceiveTextMessageHandler`, `ReceiveDeliveredReceiptHandler`) have `request.LookupKey.DirectSessionId` available at publish time

**What's NOT Implemented:**
- WPF handler for `DeliveredReceiptReceivedEvent` -- needed to update `ChatMessageModel.IsDelivered`
- `IsSending` property on `ChatMessageModel`
- Simulator chat UI (`SimulatorChatViewModel`) -- does NOT exist
- Integration of chat UI into `SimulatedPeerCardViewModel`
- Initial chat load trigger -- removed in Chunk C, not restored

**CRITICAL BUG: Session Key ≠ Conversation Key Mismatch**
`ChatViewModel.SetSession(sessionId)` receives `key.Value.ToString("N")` where `key.Value` is a `DirectSessionId` GUID. Messages are stored under this key. However, `ChatReloadCoordinator.ReloadCoreAsync` writes to `conversationId.Value.ToString("N")` -- a **different GUID**. Messages written by reload go to a list nobody reads. See Step A.0.

**DESIGN PRINCIPLE: State Service model modification**
Per `r3.readme.md` §1, state services own observable collections of mutable models. Models can be modified directly on any thread -- the ViewModel's job is to project them to the UI thread. We do NOT add "thread-safe wrapper methods" for individual property mutations. **Reactive properties already notify** (e.g., `BindableReactiveProperty<bool>` raises change events), so handlers can mutate model properties directly without an extra "state mutated" signal.

### Goal

Enable full bi-directional chat messaging between the Main Window and Simulated Peers with proper message persistence and delivery state tracking.

### Scope

This chunk addresses:

1. **Main window chat persistence**: Previous chat messages should display when the application starts
1a. **Sending state**: Messages sent from main should appear in a "sending" state until delivered
1b. **Initial load**: Chat messages should load from SQLite when a conversation is opened

2. **Simulator chat UI**: The simulator should have a UI for reading and sending chat messages to/from the main window
2a. **In-memory retention**: The simulator should maintain all chat messages in memory while the application is open
2b. **Persistence limit**: The simulator should only maintain a small maximum number of chat messages in persisted state (currently 50, which is acceptable)
2c. **Per-peer chat UI**: The simulator needs a chat UI for each simulated peer

### Current State Analysis

**Main Window Chat:**
- `ChatStateService` keys its `ConcurrentDictionary` by `string`. Currently `ChatReloadCoordinator` writes via `conversationId.Value.ToString("N")`, but `ChatViewModel.SetSession` reads via `DirectSessionId.Value.ToString("N")` -- **mismatch** (Step A.0)
- `ChatReloadCoordinator` -- missing `TimeProvider` injection (should follow `PeerConnectionReloadCoordinator` pattern)
- `ChatMessageModel` -- has `IsDelivered`, `IsRead` as `BindableReactiveProperty<bool>`. Missing `IsSending`.
- `ChatMessageSnapshot` -- uses `string Id`. Should use `MessageId`.
- Issues: key mismatch, no initial load trigger, no delivery receipt handler, no sending state

**Simulator Chat:**
- `SimulatedPeerModel.RecentChatMessages` -- `ObservableList<SimulatedChatMessageSnapshot>` with max 50
- `SimulatedPeerCardViewModel` -- rendered inline in `SimulatorPeersTabView.xaml`. No chat section.
- Issue: No UI to display or send chat messages

### Step A.0: Fix Session Key ≠ Conversation Key Mismatch + Improve Domain Typing

**Problem:** `ChatViewModel.SetSession(sessionId)` stores messages under `DirectSessionId.ToString("N")`. `ChatReloadCoordinator.ReloadCoreAsync` writes to `ConversationId.Value.ToString("N")` -- a different GUID. Messages never appear.

**Root cause:**
1. `SelectedChannelPaneViewModel.ResolveChatContent` → `key.Value` is a `DirectSessionId` GUID
2. `SessionScopeFactory.GetOrCreate(sessionId)` passes `key.Value.ToString("N")` as `string`
3. `ChatReloadCoordinator` writes under `ConversationId` key
4. Two different GUIDs → messages go to a list nobody reads

**Solution: Use `DirectSessionId` as the canonical storage key and enrich domain events.**

The `ConversationLookupKey` already carries an optional `DirectSessionId`. All three event publish sites (`PostTextMessageHandler`, `ReceiveTextMessageHandler`, `ReceiveDeliveredReceiptHandler`) have `request.LookupKey.DirectSessionId` available. Add `Guid? DirectSessionId` to the domain events so the WPF handlers can route without a reverse-lookup or DB call. This does NOT break domain isolation -- `DirectSessionId` is already a concept in the Chat domain via `ConversationLookupKey`.

**A.0.1: Re-key `ChatStateService` from `string` to `DirectSessionId`**

**File:** `Desktop.Wpf/Features/Chat/State/ChatStateService.cs`
```csharp
private readonly ConcurrentDictionary<DirectSessionId, ObservableList<ChatMessageModel>> _sessionMessages = new();
private readonly object _stateGate = new();

public ObservableList<ChatMessageModel> GetOrAddSessionMessagesList(DirectSessionId sessionId)
    => _sessionMessages.GetOrAdd(sessionId, _ => new ObservableList<ChatMessageModel>());

public void SyncMessages(DirectSessionId sessionId, IReadOnlyList<ChatMessageSnapshot> snapshots)
{
    var list = GetOrAddSessionMessagesList(sessionId);
    lock (_stateGate)
    {
        var existingById = list.ToDictionary(m => m.Id);
        foreach (var snap in snapshots)
        {
            if (existingById.TryGetValue(snap.Id, out var existing))
            {
                existing.UpdateFromSnapshot(snap);
            }
            else
            {
                list.Add(new ChatMessageModel(snap));
            }
        }
    }
}

public void OptimisticInsert(DirectSessionId sessionId, ChatMessageSnapshot snapshot)
{
    lock (_stateGate)
    {
        var list = GetOrAddSessionMessagesList(sessionId);
        if (!list.Any(m => m.Id == snapshot.Id))
        {
            list.Add(new ChatMessageModel(snapshot));
        }
    }
}

public void MarkAsDelivered(DirectSessionId sessionId, MessageId messageId)
{
    lock (_stateGate)
    {
        var list = GetOrAddSessionMessagesList(sessionId);
        var msg = list.FirstOrDefault(m => m.Id == messageId);
        if (msg is not null)
        {
            msg.IsDelivered.Value = true;
            msg.IsSending.Value = false;
        }
    }
}
```

**A.0.2: Introduce ChatMessageViewModel and update ChatViewModel.SetSession**

**Architectural decision:** Per established MVVM pattern in this codebase, pass the model into the constructor of a viewmodel type that projects the model values on the UI thread. This keeps the model thread-agnostic and lets the ViewModel handle UI thread projection.

**Thread safety note:** `ObservableList` from Cysharp has internal synchronization for reads. `CreateView`, enumeration, and property access do NOT require locking. Only structural changes (Add, Remove, Clear) require locking via `_stateGate`. This is why `ChatViewModel.SetSession` can safely call `CreateView` without acquiring the lock.

**File:** `Desktop.Wpf/Features/Chat/ChatMessageViewModel.cs` (new file)
```csharp
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Chat;

public sealed class ChatMessageViewModel : ViewModelBase
{
    public string Author => _model.Author;
    public string Text => _model.Text;
    public DateTimeOffset Timestamp => _model.Timestamp;
    public bool IsOwn => _model.IsOwn;

    public BindableReactiveProperty<bool> IsDelivered { get; }
    public BindableReactiveProperty<bool> IsRead { get; }
    public BindableReactiveProperty<bool> IsSending { get; }

    private readonly ChatMessageModel _model;
    private DisposableBag _bag;

    public ChatMessageViewModel(ChatMessageModel model, IUiDispatcher ui)
    {
        _model = model;

        // Project reactive properties to UI thread
        IsDelivered = model.IsDelivered
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsDelivered.Value)
            .AddTo(ref _bag);

        IsRead = model.IsRead
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsRead.Value)
            .AddTo(ref _bag);

        IsSending = model.IsSending
            .ObserveOnCurrentSynchronizationContext()
            .ToBindableReactiveProperty(model.IsSending.Value)
            .AddTo(ref _bag);
    }

    protected override void DisposeCore() => _bag.Dispose();
}
```

**File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
```csharp
public NotifyCollectionChangedSynchronizedViewList<ChatMessageViewModel> Messages { get; private set; }
private DirectSessionId? _sessionId;
private ISynchronizedView<ChatMessageModel, ChatMessageViewModel>? _messagesView;

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
    // 5. Do NOT sort in the ViewModel -- per wpf.readme.md §1, sorting is a XAML concern.
}
```

**XAML sorting (established pattern):** bind `ItemsSource` to the projected `NotifyCollectionChangedSynchronizedViewList<ChatMessageViewModel>` and apply a `<CollectionViewSource>` in XAML with a `SortDescription` on `Timestamp` (ascending). This avoids `collectionView.Refresh()` plumbing in the ViewModel.

**File:** `Desktop.Wpf/Features/Chat/ChatView.xaml`

Add the CollectionViewSource to the existing `UserControl.Resources` section (lines 7-9):
```xaml
<UserControl.Resources>
  <converters:DateTimeOffsetToStringConverter x:Key="DateTimeOffsetToStringConverter"/>
  <CollectionViewSource x:Key="MessagesView" Source="{Binding Messages}">
    <CollectionViewSource.SortDescriptions>
      <componentModel:SortDescription PropertyName="Timestamp" Direction="Ascending" />
    </CollectionViewSource.SortDescriptions>
  </CollectionViewSource>
</UserControl.Resources>
```

Add the namespace declaration to the root UserControl (line 1, after existing xmlns declarations):
```xaml
xmlns:componentModel="clr-namespace:System.ComponentModel;assembly=WindowsBase"
```

Update the ItemsControl binding (line 50):
```xaml
<ItemsControl ItemsSource="{Binding Source={StaticResource MessagesView}}">
```

Update `SendCommand` to use the typed `_sessionId`:
```csharp
if (_sessionId is null) return;
var messageId = MessageId.NewId();
await _mediator.Send(new PostTextMessageCommand(
    ConversationLookupKey.ForDirectSession(_sessionId.Value.Value),
    messageId,
    text,
    DateTimeOffset.UtcNow));
```

**A.0.3: Update `SessionScopeFactory` to pass `DirectSessionId`**

**File:** `Desktop.Wpf/Features/Sessions/SessionScopeFactory.cs`

Change the key from `string` to `DirectSessionId`:
```csharp
private readonly Dictionary<DirectSessionId, IServiceScope> _scopes = new();
private readonly LinkedList<DirectSessionId> _lru = new();

public SessionResolved GetOrCreate(DirectSessionId sessionId, SessionHeader? header = null)
{
    // ... LRU logic unchanged but using DirectSessionId
    var chatVm = scope.ServiceProvider.GetRequiredService<ChatViewModel>();
    chatVm.SetSession(sessionId);
    // ...
}
```

**A.0.4: Update `SelectedChannelPaneViewModel.ResolveChatContent`**

```csharp
private object? ResolveChatContent(PeerConnectionModel model, PeerConnectionKey key)
{
    var sessionId = new DirectSessionId(key.Value);
    // ...
    var resolved = _sessionFactory.GetOrCreate(sessionId, header);
    // ...
}
```

**A.0.5: Introduce Chat-domain wrapper for DirectSessionId**

**Architectural decision:** To avoid domain isolation concerns (DirectSessionId is in Percolator.Network), introduce a Chat-domain value object that wraps the GUID. This keeps Chat events pure to the Chat domain.

**File:** `Percolator.Chat/ValueObjects/DirectSessionIdValueObject.cs` (new file)
```csharp
namespace Percolator.Chat.ValueObjects;

public readonly record struct DirectSessionIdValueObject(Guid Value)
{
    public static DirectSessionIdValueObject? FromGuid(Guid? guid) =>
        guid.HasValue ? new DirectSessionIdValueObject(guid.Value) : null;

    public override string ToString() => Value.ToString();
}
```

**File:** `Percolator.Chat/Events/TextMessagePostedEvent.cs` -- add `DirectSessionIdValueObject? DirectSessionId` property

**File:** `Percolator.Chat/Events/TextMessageReceivedEvent.cs` -- add `DirectSessionIdValueObject? DirectSessionId` property

**File:** `Percolator.Chat/Events/DeliveredReceiptReceivedEvent.cs` -- add `DirectSessionIdValueObject? DirectSessionId` property

Nullable because group/PKH lookups won't have a `DirectSessionId`. Only direct-session conversations will.

**Update publish sites** to include the new field using the Chat-domain wrapper:

**File:** `Percolator.Application/Apps/Chat/Handlers/PostTextMessageHandler.cs` (line 86-95):
```csharp
await _publisher.Publish(new TextMessagePostedEvent(
    resolution.Conversation.Id.Value,
    request.MessageId.Value,
    resolution.SelfIdentityId,
    resolution.Conversation.Participants.Select(p => p.Value).ToList(),
    request.Content,
    request.SentTimestampUtc,
    Percolator.Chat.ValueObjects.DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), // NEW
    cancellationToken).ConfigureAwait(false);
```

**File:** `Percolator.Chat/App/Handlers/ReceiveTextMessageHandler.cs` (line 39-45):
```csharp
await _publisher.Publish(new TextMessageReceivedEvent(
    resolution.Conversation.Id.Value,
    request.MessageId.Value,
    resolution.SelfIdentityId,
    request.SenderId.Value,
    request.Content,
    request.SentTimestampUtc,
    Percolator.Chat.ValueObjects.DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), // NEW
    cancellationToken).ConfigureAwait(false);
```

**File:** `Percolator.Chat/App/Handlers/ReceiveDeliveredReceiptHandler.cs` (line 38-43):
```csharp
await _publisher.Publish(new DeliveredReceiptReceivedEvent(
    resolution.Conversation.Id.Value,
    request.MessageId.Value,
    resolution.SelfIdentityId,
    request.RecipientId.Value,
    request.DeliveredTimestampUtc,
    Percolator.Chat.ValueObjects.DirectSessionIdValueObject.FromGuid(request.LookupKey.DirectSessionId)), // NEW
    cancellationToken).ConfigureAwait(false);
```

**A.0.6: Update `ChatReloadCoordinator` to use `DirectSessionId`, inject `TimeProvider`, and simplify to single Subject**

**Architectural decision:** Use a single Subject with a union type instead of two separate Subjects. This reduces complexity while supporting both trigger types (conversation-based and session-based).

**File:** `Desktop.Wpf/Features/Chat/IChatReloadCoordinator.cs`
```csharp
public interface IChatReloadCoordinator : IDisposable
{
    void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId);
    void TriggerReloadForSession(DirectSessionId sessionId);
}
```

**File:** `Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs`
```csharp
private sealed record ReloadTrigger(ConversationId? ConversationId, int? SelfIdentityId, DirectSessionId SessionId);

public sealed class ChatReloadCoordinator : IChatReloadCoordinator
{
    private readonly Subject<ReloadTrigger> _reloadTrigger = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ChatStateService _state;
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;
    private readonly ActiveIdentityContext _activeIdentity;
    private DisposableBag _bag;

    public ChatReloadCoordinator(
        IServiceScopeFactory scopeFactory,
        ChatStateService state,
        ISelfParticipantIdProvider selfParticipantIdProvider,
        ActiveIdentityContext activeIdentity,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _selfParticipantIdProvider = selfParticipantIdProvider;
        _activeIdentity = activeIdentity;

        _reloadTrigger
            .Debounce(TimeSpan.FromMilliseconds(50), timeProvider)
            .SelectAwait(async (trigger, ct) =>
            {
                if (trigger.ConversationId.HasValue && trigger.SelfIdentityId.HasValue)
                {
                    await ReloadCoreAsync(trigger.ConversationId.Value, trigger.SelfIdentityId.Value, trigger.SessionId, ct).ConfigureAwait(false);
                }
                else
                {
                    await ReloadFromSessionAsync(trigger.SessionId, ct).ConfigureAwait(false);
                }
                return Unit.Default;
            }, AwaitOperation.Drop)
            .Subscribe()
            .AddTo(ref _bag);
    }

    public void TriggerReloadForConversation(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId)
        => _reloadTrigger.OnNext(new ReloadTrigger(conversationId, selfIdentityId, sessionId));

    public void TriggerReloadForSession(DirectSessionId sessionId)
        => _reloadTrigger.OnNext(new ReloadTrigger(null, null, sessionId));

    // ... ReloadCoreAsync and ReloadFromSessionAsync (see Step A.1)
}
```

### Step A.1: Restore initial chat load from SQLite

**Problem:** After Chunk C, the initial reload trigger was removed. Previous messages don't load when opening a chat.

**Solution:** Implement `ReloadFromSessionAsync` and `ReloadCoreAsync` in `ChatReloadCoordinator` (pipelines already wired in Step A.0.6). Trigger from `SelectedChannelPaneViewModel`.

**File:** `Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs` -- implement the two reload methods:

```csharp
private async Task ReloadFromSessionAsync(DirectSessionId sessionId, CancellationToken ct)
{
    using var scope = _scopeFactory.CreateScope();
    var resolver = scope.ServiceProvider.GetRequiredService<IConversationResolver>();
    var lookupKey = ConversationLookupKey.ForDirectSession(sessionId.Value);
    var resolution = await resolver.ResolveAsync(lookupKey, ct).ConfigureAwait(false);

    SyncConversationToState(resolution, sessionId);
}

private async Task ReloadCoreAsync(ConversationId conversationId, int selfIdentityId, DirectSessionId sessionId, CancellationToken ct)
{
    using var scope = _scopeFactory.CreateScope();
    var repo = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
    var conversation = await repo.GetByIdAsync(conversationId, selfIdentityId).ConfigureAwait(false);
    if (conversation is null) return;

    var selfParticipantId = _selfParticipantIdProvider.Get();
    var snapshots = conversation.Messages.Select(m => new ChatMessageSnapshot(
        Id: new MessageId(m.Id.Value),
        Author: m.SenderId == selfParticipantId ? "Me" : "Peer",
        Text: m.Content,
        Timestamp: m.Timestamp,
        IsOwn: m.SenderId == selfParticipantId,
        IsDelivered: false,
        IsRead: m.ReadReceipts.Any(r => r.ReaderId == selfParticipantId)
    )).ToList();

    _state.SyncMessages(sessionId, snapshots);
}

private void SyncConversationToState(ConversationResolution resolution, DirectSessionId sessionId)
{
    var selfParticipantId = _selfParticipantIdProvider.Get();
    var snapshots = resolution.Conversation.Messages.Select(m => new ChatMessageSnapshot(
        Id: new MessageId(m.Id.Value),
        Author: m.SenderId == selfParticipantId ? "Me" : "Peer",
        Text: m.Content,
        Timestamp: m.Timestamp,
        IsOwn: m.SenderId == selfParticipantId,
        IsDelivered: false,
        IsRead: m.ReadReceipts.Any(r => r.ReaderId == selfParticipantId)
    )).ToList();

    _state.SyncMessages(sessionId, snapshots);
}
```

**Key notes:**
- `ChatMessageSnapshot.Id` is now `MessageId` (not `string`) -- see Step A.3 for the type change
- `ParticipantId` supports `==` comparison (it's a `readonly record struct`)
- `ConversationResolution` is returned by `IConversationResolver.ResolveAsync` -- it auto-creates the conversation if needed
- Both `Debounce` calls pass `timeProvider` (wired in A.0.6)

**File:** `Desktop.Wpf/Features/Sessions/SelectedChannelPaneViewModel.cs`

Inject `IChatReloadCoordinator` and trigger in `ResolveChatContent`:
```csharp
public SelectedChannelPaneViewModel(
    SelectedChannelModel selection,
    PeerConnectionStateService stateService,
    ISessionScopeFactory sessionFactory,
    SelectedPeerConnectionStateCache stateCache,
    IChatReloadCoordinator reloadCoordinator) // NEW
```

```csharp
private object? ResolveChatContent(PeerConnectionModel model, PeerConnectionKey key)
{
    var sessionId = new DirectSessionId(key.Value);
    // ...
    var resolved = _sessionFactory.GetOrCreate(sessionId, header);

    // Trigger initial load from SQLite on background thread
    _reloadCoordinator.TriggerReloadForSession(sessionId);
    // ...
}
```

### Step A.2: Handle DeliveredReceiptReceivedEvent -- update model directly

**Problem:** Messages start with `IsDelivered = false` but never get updated to `true` when delivered.

**Design principle:** Per `r3.readme.md` §1, state services own observable collections of **mutable models**. Models can be modified directly on any thread. We do NOT add "thread-safe wrapper methods" to `ChatStateService` for individual property mutations. The MediatR handler finds the model in the observable list and sets the property directly. Because `IsDelivered`/`IsSending` are `BindableReactiveProperty<bool>`, they already raise change notifications for the UI.

**File:** `Desktop.Wpf/Features/Chat/Handlers/DeliveredReceiptReceivedEventHandler.cs` (new file)

```csharp
using MediatR;
using Percolator.Chat.Events;
using Percolator.Chat.ValueObjects;
using Percolator.Network;
using Desktop.Wpf.Features.Chat.State;

namespace Desktop.Wpf.Features.Chat.Handlers;

public sealed class DeliveredReceiptReceivedEventHandler : INotificationHandler<DeliveredReceiptReceivedEvent>
{
    private readonly ChatStateService _state;

    public DeliveredReceiptReceivedEventHandler(ChatStateService state)
    {
        _state = state;
    }

    public Task Handle(DeliveredReceiptReceivedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.DirectSessionId is not { } dsid) return Task.CompletedTask;

        var sessionId = new DirectSessionId(dsid.Value);
        var messageId = new MessageId(notification.MessageId);

        // Mark as delivered via public method (handles locking internally)
        _state.MarkAsDelivered(sessionId, messageId);
        return Task.CompletedTask;
    }
}
```

**DI registration:** None needed. MediatR auto-discovers `INotificationHandler<T>` from the assembly.

### Step A.3: Add IsSending state + improve domain typing in ChatMessageModel

**Problem:** Messages sent from main should appear in a "sending" state until delivered.

**Race condition analysis:** `TextMessagePostedEvent` fires AFTER `PostTextMessageHandler` has persisted the message to SQLite. The message does NOT yet exist in `ChatStateService`'s in-memory list (it appears after `SyncMessages` during reload). So we use **optimistic insert**: the handler directly adds a `ChatMessageModel` with `IsSending = true` to the observable list, then triggers reload. `SyncMessages` merges by ID, preserving `IsSending`.

**A.3.1: Update `ChatMessageSnapshot` to use domain types**

**File:** `Desktop.Wpf/Features/Chat/ChatMessageModel.cs`
```csharp
public sealed record ChatMessageSnapshot(
    MessageId Id, 
    string Author, 
    string Text, 
    DateTimeOffset Timestamp, 
    bool IsOwn, 
    bool IsDelivered, 
    bool IsRead,
    bool IsSending = false);
```

**A.3.2: Update `ChatMessageModel`**

**File:** `Desktop.Wpf/Features/Chat/ChatMessageModel.cs`
```csharp
public sealed class ChatMessageModel : IDisposable
{
    private DisposableBag _bag;

    public MessageId Id { get; }
    public string Author { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsOwn { get; }

    public BindableReactiveProperty<bool> IsDelivered { get; }
    public BindableReactiveProperty<bool> IsRead { get; }
    public BindableReactiveProperty<bool> IsSending { get; }

    public ChatMessageModel(ChatMessageSnapshot snapshot)
    {
        Id = snapshot.Id;
        Author = snapshot.Author;
        Text = snapshot.Text;
        Timestamp = snapshot.Timestamp;
        IsOwn = snapshot.IsOwn;

        IsDelivered = new BindableReactiveProperty<bool>(snapshot.IsDelivered).AddTo(ref _bag);
        IsRead = new BindableReactiveProperty<bool>(snapshot.IsRead).AddTo(ref _bag);
        IsSending = new BindableReactiveProperty<bool>(snapshot.IsSending).AddTo(ref _bag);
    }

    public void UpdateFromSnapshot(ChatMessageSnapshot snapshot)
    {
        // Do not let a DB snapshot overwrite a 'true' state with a 'false' state
        if (snapshot.IsDelivered) IsDelivered.Value = true;
        if (snapshot.IsRead) IsRead.Value = true;
        // IsSending is NOT overwritten -- managed by optimistic insert + DeliveredReceiptReceivedEventHandler
    }

    public void Dispose() => _bag.Dispose();
}
```

**Note:** `ChatMessageViewModel` was introduced in Step A.0.2. The XAML bindings already use `.Value` for reactive properties (e.g., `IsDelivered.Value`), so no changes are needed to ChatView.xaml DataTemplate bindings.

**A.3.3: Update `ChatStateService.SyncMessages`**

`SyncMessages` already uses `existingById.TryGetValue` to merge -- `UpdateFromSnapshot` doesn't overwrite `IsSending`, so it's naturally preserved. No changes needed to `SyncMessages` beyond the re-key from `string` to `DirectSessionId` done in A.0.1.

**A.3.4: Update `ChatStateUpdateHandlers` -- optimistic insert via public method**

**Architectural decision:** Lock only around structural changes to ObservableList (adding/removing items). Do NOT lock around property updates on existing models -- those are reactive and thread-safe. Encapsulate locking within ChatStateService via public methods.

**File:** `Desktop.Wpf/Features/Chat/Handlers/ChatStateUpdateHandlers.cs`

```csharp
public sealed class ChatStateUpdateHandlers :
    INotificationHandler<TextMessagePostedEvent>,
    INotificationHandler<TextMessageReceivedEvent>
{
    private readonly IChatReloadCoordinator _reload;
    private readonly ChatStateService _state;

    public ChatStateUpdateHandlers(IChatReloadCoordinator reload, ChatStateService state)
    {
        _reload = reload;
        _state = state;
    }

    public Task Handle(TextMessagePostedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.DirectSessionId is not { } dsid) return Task.CompletedTask;

        var sessionId = new DirectSessionId(dsid.Value);
        var conversationId = new ConversationId(notification.ConversationId);
        var messageId = new MessageId(notification.MessageId);

        // Optimistic insert via public method (handles locking internally)
        _state.OptimisticInsert(sessionId, new ChatMessageSnapshot(
            Id: messageId,
            Author: "Me",
            Text: notification.Content,
            Timestamp: notification.SentTimestampUtc,
            IsOwn: true,
            IsDelivered: false,
            IsRead: false,
            IsSending: true));

        // Trigger reload to merge full history from DB
        _reload.TriggerReloadForConversation(conversationId, notification.SenderSelfIdentityId, sessionId);
        return Task.CompletedTask;
    }

    public Task Handle(TextMessageReceivedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.DirectSessionId is not { } dsid) return Task.CompletedTask;

        var sessionId = new DirectSessionId(dsid.Value);
        _reload.TriggerReloadForConversation(
            new ConversationId(notification.ConversationId),
            notification.SelfIdentityId,
            sessionId);
        return Task.CompletedTask;
    }
}
```

**Sending state lifecycle:**
1. User sends → `PostTextMessageCommand` → DB write → `TextMessagePostedEvent` (now includes `DirectSessionId`)
2. Handler: optimistic insert with `IsSending = true` → message appears immediately in UI
3. Handler: triggers reload → `SyncMessages` merges by `MessageId`, `UpdateFromSnapshot` does NOT overwrite `IsSending`
4. Delivery receipt → `DeliveredReceiptReceivedEventHandler` sets `IsDelivered = true`, `IsSending = false`
5. UI binding: show spinner when `IsSending.Value && !IsDelivered.Value`

### Step A.4: Create simulator chat UI for each peer

**Problem:** The simulator has no UI to display or send chat messages.

**Solution:** Create a `SimulatorChatViewModel` for each simulated peer. Rendered inline in `SimulatorPeersTabView.xaml` DataTemplate (no separate `SimulatedPeerCardView.xaml`).

**File:** `Desktop.Wpf/Features/Simulator/SimulatorChatViewModel.cs` (new file)

```csharp
using ObservableCollections;
using R3;
using Desktop.Wpf.Shared.Mvvm;

namespace Desktop.Wpf.Features.Simulator;

public sealed class SimulatorChatViewModel : ViewModelBase
{
    public BindableReactiveProperty<string> MessageInput { get; }
    public AsyncRelayCommand SendMessageCommand { get; }
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
        
        SendMessageCommand = new AsyncRelayCommand(async _ =>
        {
            var text = MessageInput.Value;
            if (string.IsNullOrWhiteSpace(text)) return;
            
            await _state.SendChatMessageToMainAsync(_model.PeerId, text, CancellationToken.None);
            MessageInput.Value = string.Empty;
        }, _ => !string.IsNullOrWhiteSpace(MessageInput.Value));
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
```

**Pattern notes:**
- `DisposableBag _bag` -- struct, no `new` needed (matches `SimulatedPeerCardViewModel`)
- `Messages` -- concrete `NotifyCollectionChangedSynchronizedViewList<T>` (no interface variant exists)
- `Messages.Dispose()` explicitly in `DisposeCore` -- per `wpf.readme.md` §3
- No `ViewMappings.xaml` entry -- embedded inline, not resolved by implicit DataTemplate
- XAML binding: `{Binding MessageInput.Value, UpdateSourceTrigger=PropertyChanged}` -- `.Value` required for `BindableReactiveProperty`

### Step A.5: Integrate simulator chat UI into SimulatedPeerCardViewModel

**Problem:** The simulator peer card needs to display the chat UI.

**Solution:** Add a `SimulatorChatViewModel` property to `SimulatedPeerCardViewModel` and embed the chat UI in the existing `SimulatorPeersTabView.xaml` DataTemplate. There is **no** `SimulatedPeerCardView.xaml` -- peer cards are rendered inline.

**File:** `Desktop.Wpf/Features/Simulator/SimulatedPeerCardViewModel.cs`

1. Add property and construction (constructor already has `_model`, `_state`, and `_ui`):
```csharp
public SimulatorChatViewModel ChatViewModel { get; }

// In constructor body:
ChatViewModel = new SimulatorChatViewModel(_model, _state, _ui);
```

2. Dispose in `Dispose()` method (SimulatedPeerCardViewModel implements IDisposable directly, not via ViewModelBase):
```csharp
// In Dispose():
ChatViewModel.Dispose();
```

**File:** `Desktop.Wpf/Features/Simulator/SimulatorPeersTabView.xaml`

Add a chat `Expander` inside the peer card DataTemplate, after the existing "Pre-key publishing" Expander (after line 113, which is the closing `</Expander>` tag):
```xaml
<Expander Margin="0,12,0,0" Header="Chat">
  <StackPanel Margin="0,10,0,0">
    <ListBox MaxHeight="200" ItemsSource="{Binding ChatViewModel.Messages}">
      <ListBox.ItemTemplate>
        <DataTemplate>
          <StackPanel Margin="4">
            <TextBlock FontWeight="SemiBold" Text="{Binding Direction}"/>
            <TextBlock Text="{Binding Content}" TextWrapping="Wrap"/>
            <TextBlock Foreground="#888" FontSize="10" Text="{Binding Timestamp, StringFormat='{}yyyy-MM-dd HH:mm:ss'}"/>
          </StackPanel>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
    <DockPanel Margin="0,6,0,0">
      <Button DockPanel.Dock="Right" Content="Send" Command="{Binding ChatViewModel.SendMessageCommand}" />
      <TextBox Text="{Binding ChatViewModel.MessageInput.Value, UpdateSourceTrigger=PropertyChanged}" />
    </DockPanel>
  </StackPanel>
</Expander>
```

**Key notes:**
- Binding uses `ChatViewModel.MessageInput.Value` (not `ChatViewModel.MessageInput`) -- `.Value` is required for `BindableReactiveProperty`
- `SimulatedPeerCardViewModel` already injects `IUiDispatcher` as `_ui`, so passing it to `SimulatorChatViewModel` is straightforward
- `SimulatedPeerCardViewModel` already injects `ISimulatorStateService` as `_state`

### Step A.6: Verify simulator chat message persistence limits

**Verification:** `SimulatedPeerModel.AddChatMessage` enforces max 50 messages via `RemoveAt(0)` when count exceeds 50. `JsonSimulatorStateRepository` persists the `RecentChatMessages` list in the snapshot.

**No changes needed** -- requirement 2b already satisfied.

### Step A.7: Verification

**Manual verification steps:**

1. **Main window initial load:**
   - Start application, open a chat with a peer that has existing messages
   - Verify previous messages display (validates Step A.0 key fix + Step A.1 reload)

2. **Sending state:**
   - Send a message from main window
   - Verify message appears immediately with sending indicator
   - Verify indicator clears when delivered receipt arrives

3. **Delivery receipt:**
   - Confirm `IsDelivered` updates to `true` on receipt (validates Step A.2)

4. **Simulator chat UI:**
   - Open simulator tab, expand "Chat" on a peer card
   - Send message from simulator → verify it appears in both simulator and main window
   - Send message from main → verify it appears in simulator chat

5. **Simulator persistence:**
   - Send >50 messages, restart app
   - Verify only last 50 persisted for simulator

6. **Key mismatch regression:**
   - Open a conversation, send a message, close and reopen
   - Verify messages still display (validates `DirectSessionId` keying from A.0)

---


## Chunk D

...
### Goal

Replace many naked `byte[]` PKH parameters/fields (especially in simulator relay routing and related tests) with a well-named, explicit PKH type.

### Constraints / Non-Goals

- Simulator remains in-memory and persists simulated peers between restarts (do not mingle simulator peer data with main window peer store).
- Do not do project-wide renames in this chunk.

### Investigation: existing PKH types

There is already a `Pkh` value object in `Percolator.Chat.ValueObjects`:

- `Percolator.Chat/ValueObjects/Pkh.cs`
    - Implements `ByteArrayRecord` equality (SequenceEqual + stable hash)
    - Does not enforce 32-byte length
    - Lives in Chat domain, so it is NOT suitable as the canonical PKH type for Identity/Network/Simulator relay routing.

There is also a `PublicKeyHash` value object in `Percolator.Network`:

- `Percolator.Network/PublicKeyHash.cs`
    - Implements `ByteArrayRecord` equality
    - Does not enforce 32-byte length
    - Lives in Network domain, so it is NOT suitable as the canonical PKH type for Identity.

### Design decision

Create an Identity-domain PKH type that is explicit about what it represents and enforces invariants:

- Consistent name: `IdentityPublicKeyHash`
- Location: `Percolator.Identity` project

Suggested shape (concrete proposal):

- Backing storage uses `ReadOnlyMemory<byte>` to avoid accidental mutation by callers.
- Provide explicit construction and boundary conversions:
    - `static IdentityPublicKeyHash FromBytes(byte[] bytes)`
        - validates non-null and `Length == 32`
        - copies into an internal buffer
    - `static IdentityPublicKeyHash FromSpki(byte[] spki)`
        - computes `SHA256.HashData(spki)`
        - delegates to `FromBytes(...)`
    - `byte[] ToArray()`
        - returns a copy for boundary APIs that still require `byte[]`

Preferred computation policy:

- Any time we currently do `SHA256.HashData(spki)` in feature code, replace it with `IdentityPublicKeyHash.FromSpki(spki)` once Chunk D is complete.

### Work items

**D.1: Add the new PKH type**

- Add `Percolator.Identity/IdentityPublicKeyHash.cs`
- Implement:
    - `FromBytes(byte[])` validation (`ArgumentNullException`, `ArgumentException` for non-32)
    - `FromSpki(byte[])` (uses `System.Security.Cryptography.SHA256.HashData`)
    - Optional: `ToHexString()` helper (only if already using hex formatting elsewhere; otherwise defer)

**D.2: Add adapters at the boundaries**

Update `Percolator.Identity/IPeerPublicSigningKeyStore.cs` to add *non-breaking* overloads (keep the existing `byte[]` APIs until migration is complete):

- Add overloads:
    - `Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, IdentityPublicKeyHash publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default);`
    - `Task<PeerId?> GetPeerIdByPublicKeyHashAsync(IdentityPublicKeyHash publicKeyHash, CancellationToken ct = default);`
    - `Task<IdentityPublicKeyHash?> GetPublicKeyHashByPeerIdAsync(PeerId peerId, CancellationToken ct = default);`

Then update the store implementation(s) in Infrastructure to implement these overloads by delegating to existing `byte[]` implementations.

**D.3: Gradually migrate application/simulator call sites**

Prioritize call sites where PKH is central to correctness and where we currently have `Length == 32` guards:

- `Percolator.Application/Network/ISimulatorOutboundInterceptor.cs`
    - Change `TryRouteMessageViaSimulatorRelayAsync(byte[] recipientPublicKeyHash, ...)`
    - To: `TryRouteMessageViaSimulatorRelayAsync(IdentityPublicKeyHash recipientPublicKeyHash, ...)`

- `Desktop.Wpf/Features/Simulator/ISimulatorStateService.cs`
    - Change `TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, ...)`
    - To: `TryGetPeerIdByIdentityPublicKeyHashAsync(IdentityPublicKeyHash recipientPublicKeyHash, ...)`

- `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`
    - Remove `recipientPublicKeyHash.Length != 32` guard; rely on `IdentityPublicKeyHash`.

- `Desktop.Wpf/Features/Simulator/Models/SimulatedRelayModel.cs`
    - Change `InboundRelayMessage.TargetPkh: byte[]` to `IdentityPublicKeyHash`
    - Remove non-empty checks and replace with type invariant

- Persistence boundary:
    - `Desktop.Wpf/Features/Simulator/*Snapshot*.cs` and JSON repository should remain `byte[]` (serialized) but convert at boundaries.

Explicit migration sequencing:
1) Introduce `IdentityPublicKeyHash` and store overloads.
2) Update simulator-facing APIs and models to typed PKH.
3) Update `MessageService` to call the typed overload from `_keyStore.GetPublicKeyHashByPeerIdAsync` and only convert to `byte[]` at protocol boundaries.

---

## Chunk E

### Goal

Build a Desktop.Wpf-only send pipeline (decorator chain) so simulator-specific routing logic does not live in the core `MessageService` hot path.

This aligns with:

- Simulator is a local in-memory simulation of remote peers
- Eventually simulator becomes a separate process and this fork disappears

### Proposed architecture

**E.1: Introduce an outbound send abstraction in Application layer**

Add a single seam that Desktop.Wpf can decorate without forking `MessageService`:

- In `Percolator.Application/Network` add:
  - `public interface IOutboundCipherSender`
    - `Task SendAsync(byte[] cipherBytes, PeerId recipientPeerId, string? debugType, CancellationToken ct = default);`

Then:

- `MessageService` depends on `IOutboundCipherSender` for the final delivery step.
- `MessageService` remains responsible for:
  - resolving sessions
  - encrypting `InternalEnvelope` -> `cipherBytes`
  - providing `debugType` (currently derived from `envelope.ChatEnvelope?.MessageCase.ToString()`)

**E.2: Default sender implementation (Application)**

Implement `NetworkOutboundCipherSender : IOutboundCipherSender` in `Percolator.Application/Network` that performs what `MessageService` currently does after encryption:

- Wire tap (moved out of `MessageService`)
- Network send

Goal: after this chunk, `MessageService` should not invoke `IOutboundMessageWireTap` directly; the default cipher sender owns wire tap behavior.

DI: register `IOutboundCipherSender` to `NetworkOutboundCipherSender` in `Percolator.Application/Network/ServiceCollectionExtensions.cs`.

**E.3: Desktop.Wpf adds simulator decorator**

In Desktop.Wpf composition root, decorate `IOutboundCipherSender` with:

- `SimulatorRelayOutboundCipherSenderDecorator : IOutboundCipherSender`
  - Dependencies:
    - inner `IOutboundCipherSender`
    - `ISimulatorOutboundInterceptor`
    - `IPeerPublicSigningKeyStore`
  - Behavior:
    1) Resolve `IdentityPublicKeyHash?` via `_keyStore.GetPublicKeyHashByPeerIdAsync(recipientPeerId, ct)`
    2) If null: call inner sender (normal network path)
    3) If non-null: call `_simulatorOutboundInterceptor.TryRouteMessageViaSimulatorRelayAsync(pkh, cipherBytes, debugType, ct)`
       - If routed: return without calling inner sender
       - If not routed: call inner sender

This moves simulator-only routing out of `MessageService` while preserving the current behavior.

### Notes

- Keep the decorator registration in Desktop.Wpf only.
- Ensure Application layer has no reference to Desktop.Wpf types.
- When simulator becomes a separate process, delete the decorator and the seam stays useful for other cross-cutting behaviors.

---

## Chunk F

### Goal

After Chunk D (typed PKH) is implemented, improve correctness and consistency across the Chunk C feature set, especially unit tests and guard logic.

### Improvements (assuming typed PKH exists)

**Current state note:** You have already performed a rename pass using tooling and the solution currently builds with tests passing. Chunk F is now focused on finishing typed PKH adoption and tightening tests (not chasing rename fallout).

**F.1: Remove PKH length checks from internal logic**

Replace `if (pkh.Length != 32) return false;` style checks with type safety:

- `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`
  - Remove `recipientPublicKeyHash.Length != 32` guard

- `Desktop.Wpf/Features/Simulator/Models/SimulatedRelayModel.cs`
  - Replace `TargetPkh.Length == 0` checks with `IdentityPublicKeyHash` construction at boundaries

Keep validation only where raw bytes enter the system:

- protobuf parsing (`ByteString.ToByteArray()`)
- JSON persistence snapshots

**F.2: Remove direct `SequenceEqual` usage in feature logic**

- Use typed PKH equality semantics.

**F.3: Unit test improvements**

Introduce a test helper (in test projects) that produces deterministic `IdentityPublicKeyHash` values:

- `IdentityPublicKeyHashTestFactory.Create(byte seed)` -> `IdentityPublicKeyHash`

Update tests:

- `Desktop.Wpf.Tests/SimulatorOutboundInterceptorRelayRoutingTests.cs`
  - Use `IdentityPublicKeyHash` directly
  - Update mocks to `TryGetPeerIdByIdentityPublicKeyHashAsync`

- `Percolator.ApplicationTests/Network/SimulatorOutboundInterceptionTests.cs`
  - Add explicit assertions that when `_keyStore.GetPublicKeyHashByPeerIdAsync(recipientPeerId)` returns null, the interceptor is not called.
  - Add explicit assertions that when interceptor returns false, `_networkSender` is called.

---

## Chunk H

### Goal

Refactor all `ByteArrayRecord` implementations to use `ReadOnlyMemory<byte>` internally while retaining the `.Value` property for backward compatibility. Defensive copying happens at construction boundaries only, eliminating unnecessary array allocations in internal operations.

### Current State Analysis

The codebase has multiple `ByteArrayRecord` base classes across different domains, all using `byte[] Value` with defensive copying issues:

- `Percolator.Identity.Primitives.ByteArrayRecord` - wraps `byte[] Value`
- `Percolator.Chat.Primitives.ByteArrayRecord` - wraps `byte[] Value`
- `Percolator.Network.Primitives.ByteArrayRecord` - wraps `byte[] Value`
- `Percolator.Cryptography.Primitives.ByteArrayRecord` - wraps `byte[] Value`
- `Percolator.Dht.Primitives.ByteArrayRecord` - wraps `byte[] Value` (different pattern - no inheritance)

**Problems with current approach:**
1. Direct `byte[]` storage allows external mutation (no defensive copy on construction)
2. `Value` property exposes mutable array to callers
3. No standard `ToArray()` / `AsReadOnlyMemory()` pattern
4. Hash code computation iterates array every time

### Research Findings: Concrete ByteArrayRecord Implementations

**Percolator.Cryptography (18 types):**
- AssociatedData, ChainKey, Ciphertext, HandshakeInvitation, HandshakeResponseMessage, OneTimeKey, Plaintext, PreKey, PrivateEphemeralKey, PrivateOneTimeKey, PublicKey, PrivatePreKey, RatchetEphemeralKey, RatchetIdentityKey, RootKey, Signature, SharedSecret, SessionRatchetMessage

**Percolator.Network (7 types):**
- DirectMessagePublicKey, Payload, TlsCertificate, IdentityPublicKey, Signature, PublicKeyHash, PublicKey

**Percolator.Chat (3 types):**
- Pkh, GroupAvatar, EncryptedGroupKey

**Percolator.Dht (1 type):**
- NodeId (has length validation - must preserve)

### Research Findings: ByteArrayRecord.Value Direct Access Patterns

**Pattern 1: Length checks for validation**
- Cryptography.HandshakePlanner.cs - inv.Value.Length == 0
- Cryptography.AeadSessionCrypto.cs - localIdentityPrivate?.Value is null || localIdentityPrivate.Value.Length == 0
- Cryptography.IPreKeyBundleValidator.cs - bundle.IdentitySigningKey?.Value is null || bundle.IdentitySigningKey.Value.Length == 0
- Cryptography.CryptographyExtensions.cs - publicKey.Value.Length == 64 || publicKey.Value.Length == 65
- Dht.DhtService.cs - node.Id.Value.Length == targetId.Value.Length
- Dht.NodeId.cs - if (id1.Value.Length != id2.Value.Length)

**Pattern 2: Indexing and array manipulation**
- Cryptography.CryptographyExtensions.cs - publicKey.Value.Skip(1).Take(32).ToArray()
- Cryptography.X3dhDeriver.cs - ikA.ImportECPrivateKey(localIdentityPrivateKey.Value, out _)
- Dht.NodeId.cs - xorResult[i] = (byte)(id1.Value[i] ^ id2.Value[i])

**Pattern 3: Direct pass-through to crypto APIs**
- Cryptography.IRatchetEngine.cs - SessionRatchetMessage.GetAssociatedData((preKey, counter, prevLen), ad.Value)
- Cryptography.CryptographyExtensions.cs - ECDH import/export operations

**Pattern 4: Test assertions using .Value**
- Multiple test files access .Value directly for assertions
- CryptographyTests.SessionRatchetMessageTests.cs - retrievedKey.Value.Should().BeEquivalentTo(ratchetKey.Value)

### Design Pattern

**Key improvements (per domain, no shared infrastructure):**
1. Internal storage: `ReadOnlyMemory<byte>` instead of `byte[]`
2. Defensive copy in constructor
3. `Value` property returns defensive copy (maintains backward compatibility)
4. `ToArray()` returns defensive copy (alias for Value)
5. `AsReadOnlyMemory()` exposes read-only view without copying
6. Custom `Equals()` using `SequenceEqual`
7. Hash code computed on demand (no caching)

### H.1: Update Percolator.Identity.Primitives.ByteArrayRecord

**File:** `source/Percolator.Identity/Primitives/ByteArrayRecord.cs`
```csharp
namespace Percolator.Identity.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;

    protected ByteArrayRecord(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _value = copy;
    }

    /// <summary>
    /// Returns a defensive copy of the value as a byte array.
    /// Maintains backward compatibility with existing .Value access patterns.
    /// </summary>
    public byte[] Value => ToArray();

    /// <summary>
    /// Returns a defensive copy of the value as a byte array.
    /// </summary>
    public byte[] ToArray()
    {
        var copy = new byte[_value.Length];
        _value.Span.CopyTo(copy);
        return copy;
    }

    /// <summary>
    /// Returns the value as read-only memory without copying.
    /// </summary>
    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    /// <summary>
    /// Returns the value as a span without copying.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => _value.Span;

    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.2: Update Percolator.Chat.Primitives.ByteArrayRecord

**File:** `source/Percolator.Chat/Primitives/ByteArrayRecord.cs`
```csharp
namespace Percolator.Chat.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;

    protected ByteArrayRecord(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _value = copy;
    }

    public byte[] Value => ToArray();

    public byte[] ToArray()
    {
        var copy = new byte[_value.Length];
        _value.Span.CopyTo(copy);
        return copy;
    }

    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    public ReadOnlySpan<byte> AsSpan() => _value.Span;

    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.3: Update Percolator.Network.Primitives.ByteArrayRecord

**File:** `source/Percolator.Network/Primitives/ByteArrayRecord.cs`
```csharp
namespace Percolator.Network.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;

    protected ByteArrayRecord(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _value = copy;
    }

    public byte[] Value => ToArray();

    public byte[] ToArray()
    {
        var copy = new byte[_value.Length];
        _value.Span.CopyTo(copy);
        return copy;
    }

    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    public ReadOnlySpan<byte> AsSpan() => _value.Span;

    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.4: Update Percolator.Cryptography.Primitives.ByteArrayRecord

**File:** `source/Percolator.Cryptography/Primitives/ByteArrayRecord.cs`
```csharp
namespace Percolator.Cryptography.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;

    protected ByteArrayRecord(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _value = copy;
    }

    public byte[] Value => ToArray();

    public byte[] ToArray()
    {
        var copy = new byte[_value.Length];
        _value.Span.CopyTo(copy);
        return copy;
    }

    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    public ReadOnlySpan<byte> AsSpan() => _value.Span;

    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.5: Update Percolator.Dht.Primitives.ByteArrayRecord

**File:** `source/Percolator.Dht/Primitives/ByteArrayRecord.cs`
```csharp
namespace Percolator.Dht.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;

    protected ByteArrayRecord(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _value = copy;
    }

    public byte[] Value => ToArray();

    public byte[] ToArray()
    {
        var copy = new byte[_value.Length];
        _value.Span.CopyTo(copy);
        return copy;
    }

    public ReadOnlyMemory<byte> AsReadOnlyMemory() => _value;

    public ReadOnlySpan<byte> AsSpan() => _value.Span;

    public virtual bool Equals(ByteArrayRecord? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _value.Span.SequenceEqual(other._value.Span);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            var span = _value.Span;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.6: Update concrete implementations

All concrete ByteArrayRecord implementations already call `base(bytes)` in their constructors. No changes needed to concrete implementations - they automatically benefit from the new base class behavior.

**Types updated automatically:**
- Percolator.Chat: Pkh, GroupAvatar, EncryptedGroupKey
- Percolator.Cryptography: AssociatedData, ChainKey, Ciphertext, HandshakeInvitation, HandshakeResponseMessage, OneTimeKey, Plaintext, PreKey, PrivateEphemeralKey, PrivateOneTimeKey, PublicKey, PrivatePreKey, RatchetEphemeralKey, RatchetIdentityKey, RootKey, Signature, SharedSecret, SessionRatchetMessage
- Percolator.Network: DirectMessagePublicKey, Payload, TlsCertificate, IdentityPublicKey, Signature, PublicKeyHash, PublicKey
- Percolator.Dht: NodeId (preserves existing length validation logic)

### H.7: Update call sites for performance (optional optimization)

**Note:** Since `.Value` is retained as a computed property that returns `ToArray()`, existing code continues to work without changes. However, call sites can be updated to use `AsReadOnlyMemory()` or `AsSpan()` for zero-allocation access where appropriate.

**H.7.1: Protobuf serialization boundaries**

Where performance matters, update to avoid double allocation:
```csharp
// Before (works but allocates)
ByteString.CopyFrom(record.Value)

// After (avoids allocation)
ByteString.CopyFrom(record.AsReadOnlyMemory().Span)
```

**H.7.2: Cryptography API boundaries**

Update crypto API calls to use spans where supported:
```csharp
// Before (works but allocates)
cryptoApi.Process(record.Value)

// After (avoids allocation if API supports spans)
cryptoApi.Process(record.AsSpan())
```

**H.7.3: Test assertions**

Update test assertions to use typed equality:
```csharp
// Before (works but allocates)
Assert.That(actual.Value.SequenceEqual(expected.Value))

// After (uses type equality, no allocation)
Assert.That(actual.Equals(expected))
```

### Notes

- Each domain maintains its own ByteArrayRecord implementation (preserves domain isolation)
- `.Value` property is retained for backward compatibility
- Defensive copying happens at construction boundaries only
- Internal operations can use `AsReadOnlyMemory()` or `AsSpan()` for zero-allocation access
- This is a "big bang" change - all ByteArrayRecord base classes updated simultaneously
- No migration strategy needed - existing `.Value` access patterns continue to work

---

## Chunk I

### I.1: Simulator boundaries (non-negotiable)

The simulator is an in-process runtime used for rapid feedback and scenario authoring.

- Simulator-owned runtime state (peers/keys/sessions/relay queues/inboxes) must never be written to the main window SQLite database.
- Main-window features must not directly access simulator runtime state.
- Simulator interception and delivery must occur at the transport boundary.

The simulator is allowed to create “impossible” states for testing/debugging purposes.

### I.2: Service shape (single interface, simulator-boundary only)

Keep a single, discoverable `ISimulatorStateService` surface.

This service is simulator-boundary only:

- Allowed callers: simulator boundary components (simulator UI feature + outbound interceptor implementation).
- Disallowed callers: main-window business logic and domain services.

Enforcement is by architecture/conventions (DI composition + code review): production flows send via transport; transport interception decides simulator vs real network.

Implementation anchors:

- Transport boundary interception is implemented in `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`.
  - Detection rule: literal dotted-quad `127.77.*` is simulator-owned.
  - Routing rule: endpoint must match an existing simulated peer by exact `(Host, Port)`.
  - Delivery: calls `_state.ReceiveOpaqueMessageFromMainAsync(simulatedPeerId, request, ct)`.
- The simulator runtime state service is `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`.
- Simulator persistence is via `ISimulatorStateRepository` and `SimulatorStateSnapshot` (see `Desktop.Wpf/Features/Simulator/JsonSimulatorStateRepository.cs`).

### I.3: Option 1 — Single-writer rule (synchronization contract)

Adopt a strict single-writer rule:

- All mutations of simulator runtime state must occur inside `SimulatorStateService` under `_stateGate`.
- Callers must not mutate simulator models/collections directly.

Rationale: this keeps the UI reactive and keeps state transitions deterministic while still allowing the simulator to offer powerful “unsafe” operations.

Non-ambiguous rules for developers:

- Any method that mutates simulator-owned runtime state MUST take the `_stateGate` (use the existing `WithStateGateAsync(...)` helpers in `SimulatorStateService`).
- Do not call a `_stateGate`-taking method while holding `_stateGate` unless the call site has been explicitly refactored to release the gate first.
  - Example: `PublishStandardPreKeyBundleToRelayAsync` must not await `PublishPreKeyBundleAsync` while still holding the gate.
- If the simulator UI needs to “edit state”, it must do so by calling `ISimulatorStateService` methods (not by mutating collections directly).

### I.4: Observability approach (reactive UI without shared mutable writes)

Maintain reactive UI using existing project patterns:

- Service exposes read-oriented reactive state such as `IReadOnlyObservableList<SimulatedPeerModel>` / `IReadOnlyObservableList<SimulatedRelayModel>`.
- Peer models use `ReadOnlyReactiveProperty<T>` for state that should be observed.

Read-only reactive exposure is encouraged where practical, but the simulator may retain some foot-guns as long as the single-writer rule is upheld by convention.

Concrete guidance for what to expose:

- At the service boundary, continue exposing the existing:
  - `IReadOnlyObservableList<SimulatedPeerModel> Peers`
  - `IReadOnlyObservableList<SimulatedRelayModel> Relays`
  - `IReadOnlyObservableList<PeerRelationship> Relationships`
- Inside peer/relay models, prefer the existing pattern already used in `SimulatedPeerModel`:
  - Public read access via `ReadOnlyReactiveProperty<T>` / `IReadOnlyObservableList<T>`
  - Internal write access via `internal ...Mutable` properties

Decision (pre-made): do not add a new global state snapshot/event system at this time; continue using the reactive surfaces already present.

### I.5: “Unsafe” scenario authoring operations (explicitly named)

Add/retain simulator-only methods that may violate invariants to set up scenarios (examples):

- Force publish malformed or mismatched prekey bundles.
- Inject/corrupt/reorder relay messages.
- Force set peer online/offline, sessions, pending handshake buffers.

These operations must still be synchronized via `_stateGate`.

Naming convention: prefer `Force*` / `Unsafe*` prefixes to signal that these are simulator-authoring tools, not normal application workflows.

Decision (pre-made): unsafe operations are still required to obey `_stateGate` single-writer synchronization.

### I.6: Prekey bundle architecture (no stored serialized bytes)

The simulator must support two modes:

- **Normal mode**: publish valid-ish bundles and follow the protocol expectation that a response includes at most one one-time key.
- **Scenario authoring mode**: publish malformed / contradictory / partial data on purpose (including “invalid bytes”) to test rejection paths.

#### I.6.1: Canonical stored representation (normal mode)

Canonical storage for a published bundle is structured data persisted through the simulator snapshot:

- **Template fields** (always stored)
  - `IdentityKey` (SPKI bytes)
  - `SignedPreKeyId` (`Guid`)
  - `SignedPreKey` (SPKI bytes)
  - `PreKeySignature` (bytes)
  - `ExpiresUtc` (`DateTimeOffset`)
- **Remaining one-time keys**
  - `ObservableList<OneTimeKeyInstance>` where `OneTimeKeyInstance` contains `(Guid Id, OneTimeKey Key)`

Implementation anchors:

- Stored model type: `SimulatedPublishedPreKeyBundleModel` (see `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`).
- Persistence path:
  - `SimulatedPeerModel.Freeze()` -> `PeerStateSnapshot` -> `SimulatorStateSnapshot`
  - `JsonSimulatorStateRepository.CreateDto(...)` / `CreatePeerSnapshot(...)` round-trip the same fields.

#### I.6.2: Pop semantics (required)

Pop semantics must match the protocol expectation that responses include at most one OTK:

- Pop operation: `SimulatorStateService.TryPopPreKeyBundleByRecipientPkhAsync(relayHostPeerId, recipientPublicKeyHash, ct)`.
- Required behavior:
  - Find bundle by `RecipientPublicKeyHash` under the relay host peer.
  - If bundle is expired, remove it and return `null`.
  - If bundle exists:
    - Remove **at most one** `OneTimeKeyInstance` from the stored `OneTimeKeys` list.
    - Return a response object that contains the template + the single popped key (0/1 keys).
    - Keep the stored bundle entry with the remaining keys.

Decision (pre-made): the returned response object must never expose the remaining key pool.

#### I.6.3: Response construction (normal mode)

When responding to a bundle request, build the protobuf at the edge from the returned pop result:

- Inbound request path: `SimulatorStateService.ReceiveOpaqueMessageFromMainAsync(...)` handles `GetPreKeyBundleRequest` and constructs `GetPreKeyBundleResponse`.
- Handshake initiation path: `SimulatorStateService.InitiateStandardHandshakeToMainByRelayPkhAsync(...)` consumes a popped bundle and constructs a `Percolator.Cryptography.PreKeyBundle`.

In both paths:

- Set template fields from the stored template.
- Include **0 or 1** one-time key from the popped key.

#### I.6.4: Scenario authoring support (unsafe override bytes)

Scenario authoring needs a way to intentionally serve invalid bytes.

Decision (pre-made): add an optional raw override field on the stored bundle model:

- `byte[]? UnsafeRawPreKeyBundleBytesOverride`

Decision (pre-made): add a simulator-only authoring method on `ISimulatorStateService`:

- `ForcePublishPreKeyBundleAsync(...)` that stores the same template + OTK pool and optionally sets the override bytes.

Required behavior when override bytes are present:

- In the `GetPreKeyBundleRequest` response path, if override bytes are non-null, return those bytes as the prekey-bundle payload (even if invalid).
- Pop semantics still apply to the stored OTK pool unless the unsafe feature explicitly disables popping.

Decision (pre-made): the first implementation keeps popping enabled even when override bytes are present (simpler, deterministic).

### I.7: Unit testing strategy

Follow unit-testing guidance: test observable behavior, not internal implementation.

Add tests for:

- Pop behavior: publishing N OTKs results in N pops returning exactly one OTK each; the N+1 pop returns none; popped IDs are distinct.
- Missing PKH: pop returns null / never-response path depending on boundary.
- Expiry: expired bundles are not returned and are removed.
- Persistence round-trip: remaining OTK count survives save/load and continues popping correctly.

Concrete test locations and approach (pre-made):

- Place tests in `Desktop.Wpf.Tests` alongside existing simulator tests:
  - `SimulatorStateServiceInitializationTests.cs` for state/persistence and publish/pop behavior
- Use `RepositoryStub` pattern for deterministic initialization (do not depend on filesystem I/O).
- Prefer testing via `ISimulatorStateService` methods; avoid inspecting private fields.

Concrete test cases (exact assertions):

- `PublishPreKeyBundle_WithMultipleOnetimeKeys_PopsOneAtATime`
  - Publish 3 OTKs
  - Pop 4 times
  - Assert OTK counts: 1,1,1,0
  - Assert popped OTK ids are distinct
- `TryPopPreKeyBundle_MissingPkh_ReturnsNull`
- `TryPopPreKeyBundle_ExpiredBundle_ReturnsNullAndRemovesBundle`
- `ForcePublishPreKeyBundle_WithUnsafeOverride_ReturnsRawBytes`
  - Publish with override bytes
  - Request bundle
  - Assert the response bytes match the override (byte-for-byte)

Persistence test (exact):

- Publish N, pop K, force save, reinitialize from saved snapshot, then pop again and confirm the remaining pool is `N-K-1`.

---

## Chunk J


### J.1: Problem statement (observed failure)

After a **relayed standard handshake** is established (`main -> sim relay -> target`), sending a chat message to the target can fail in `Percolator.Network.Messaging.DefaultNetworkSender.SendAsync(...)` because:

- `IPeerRoutingProfileRepository.GetByIdAsync(target)` returns null (no row), so the sender can’t plan any route.
- `IRelayTopology` is currently implemented (`DefaultRelayTopology`) by also reading `IPeerRoutingProfileRepository`, so it cannot provide a relay route if the profile row is missing.

The root cause is that the **standard-handshake finalize path** (`InitiatorFinalizeService.TryFinalizeFromEstablishSessionResponseAsync`) historically does not upsert a routing profile for the newly-identified peer, while the invite finalize path does so (direct-only).

### J.2: Constraints / invariants (explicit)

- Do not persist unsolicited **inbound** handshake attempts (originating outside Main Window) to SQLite before the user accepts them.
- Outbound, Main Window initiated connection attempts may persist minimal “pending outbound” state to allow slow-path finalize.
- Routing profile mutation should be explicit and attributable to:
  - user-initiated outbound attempts, or
  - explicit user acceptance of inbound attempts.
- Architecture goal: route persistence should not be a hidden side-effect scattered across multiple flows.

### J.3: Current data flow (as implemented today)

Relayed standard handshake initiated by Main Window (implementation anchors):

- `Desktop.Wpf/Features/Sessions/Handlers/ConnectViaNetworkCommandHandler.cs`
  - Writes `PreHandshakeRecord` to `IPreHandshakeSessionStore` (SQLite)
  - Writes `SentInvitation` to `ISentInvitationRepository` (SQLite)
    - `InviteRouteKind.Relayed`
    - `InviteRelayHostPeerId = relayHostPeerId`
  - Sends `HandshakeInitiatorHello` to relay host’s message queue

Relayed establish-session response finalize:

- `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs`
  - Parses bytes as `EstablishSessionResponse`
  - Calls `IInitiatorFinalizeService.TryFinalizeFromEstablishSessionResponseAsync(...)`


Finalization currently:

- `InitiatorFinalizeService.TryFinalizeFromEstablishSessionResponseAsync(...)`
  - Matches a pending `PreHandshakeRecord` by recipient PKH
  - Ensures a stable `PeerIdentity` exists (assigns a `Percolator.Identity.PeerId`)
  - Creates session + writes DirectSession mapping
  - **Does not upsert** a `PeerRoutingProfile` for that peer (therefore no relay route can be planned)

### J.4: Phase split (laser focus)

- Phase 1 goal: enable chat send to a relayed peer even when `PeerRoutingProfiles` has no row for the target.
- Phase 2 goal: promote candidates into confirmed profiles + prune candidates.

### J.5: Candidate routes + confirmed routes (Option B2)

Decision (pre-made): adopt **Option B2**.

- `PeerRoutingProfiles` becomes **confirmed routes only** (routes that have worked at least once).
- Introduce a new persistence store for **candidate routes** (allowed-to-try, may be wrong): `PeerRouteCandidates`.

Problem (researched): `SecureSessionCreatedNotification` carries `RemotePeerId` but does not carry enough data to upsert a route. Also, acceptance flows delete their pending records; therefore we need an explicit place to persist candidate routes.

### J.5.1: New candidate store (explicit schema)

- Add a new repository + table: `IPeerRouteCandidateRepository` backed by `SqlitePeerRouteCandidateRepository`.
- New table: `PeerRouteCandidates`.

Suggested columns (pre-made):

- `Id` (long, autoincrement)
- `SelfIdentityId` (int, NOT NULL)
- `RemotePeerId` (Guid, NOT NULL)
- `RouteKind` (int: Direct|Relayed, NOT NULL)
- `EndpointHost` (string?, for direct)
- `EndpointPort` (int?, for direct)
- `RelayHostPeerId` (Guid?, for relayed)
- `ObservedAtUtc` (DateTimeOffset, NOT NULL)
- `LastAttemptAtUtc` (DateTimeOffset?)
- `LastSuccessAtUtc` (DateTimeOffset?)
- `AttemptCount` (int, NOT NULL) - initialize to 0 for new candidates
- `LastError` (string?)
- `Source` (string, NOT NULL) - use "main-initiated" for main-initiated candidates, "outside-initiated" for incoming requests

Uniqueness constraint (pre-made): unique index on `(SelfIdentityId, RemotePeerId, RouteKind, EndpointHost, EndpointPort, RelayHostPeerId)`.

### J.5.2: Where candidates are written (concrete)

- Inbound invite acceptance (`Percolator.Application/Network/ApprovePendingSessionCommand.cs`):
  - After user acceptance is validated, persist a candidate route:
    - Direct: candidate `DnsEndPoint(pending.CallbackEndpointHost, pending.CallbackEndpointPort.Value)`
    - Relayed: candidate `RelayHostPeerId = pending.RelayHostPeerId`
  - This satisfies the constraint “no unsolicited inbound persistence before acceptance” because this write occurs only on acceptance.

- Outbound relayed standard handshake attempt initiation (`Desktop.Wpf/Features/Sessions/Handlers/ConnectViaNetworkCommandHandler.cs`):
  - Persist a candidate relay route for the *target peer* only after `peerIdentity` is known.
  - Since peer identity isn’t known at initiate time, this candidate write happens at finalize time (see below).

- Outbound finalize (`Percolator.Application/Network/Handshake/InitiatorFinalizeService.cs`):
  - Once `peerIdentity` is resolved, persist candidate route derived from `SentInvitation`:
    - Direct: `TargetEndpointHost/Port`
    - Relayed: `InviteRelayHostPeerId`

### J.5.3: Phase 1 — sender fallback to candidates (concrete)

Approach 1 decision (pre-made): teach `DefaultNetworkSender` to fall back to candidates.

- When `PeerRoutingProfiles` yields no plan (profile missing or planner selects no usable route), `DefaultNetworkSender.SendAsync(...)` should query `IPeerRouteCandidateRepository` for candidates and build a best-effort plan:
  - Hard limit: try at most **3** candidates per send
  - Sort by `LastSuccessAtUtc desc` then `ObservedAtUtc desc`
  - Ordering rule:
    - If any relay candidates exist => try relay candidates first, then direct
    - Else => try direct candidates first, then relay

### J.5.4: Phase 2 — promotion to confirmed routing profiles (defer)

Phase note (pre-made): promotion is Phase 2. Phase 1 does not require promotion to achieve the relayed-chat-send goal.

Direct endpoint selection (researched):

- `GrpcMessageTransportService.SendMessageAsync(...)` selects a `GrpcEndPoint` using `IProfileRoutePlanner.SelectRoute(profile)` and throws if no endpoint exists.
- `ITransportPort.SendDirectAsync(...)` explicitly says endpoint selection is an implementation detail and does not return which endpoint was used.

Decision (pre-made): use Option A for direct confirmation attribution.

- Extend `ITransportPort` to surface the used endpoint (host/port) on success.
- Prefer a named result type over a tuple.

Phase 2 implementation anchor (pre-made):

- Add a record in `Percolator.Network.Messaging`:
  - `public sealed record TransportSendResult(bool Ok, NetworkPayload? ResponsePayload, SendFailureReason? Reason, Exception? Error, System.Net.DnsEndPoint? UsedEndpoint);`
- Update `ITransportPort`:
  - `SendDirectAsync(...)` => `Task<TransportSendResult> SendDirectAsync(...)`
  - `SendViaRelayAsync(...)` => `Task<TransportSendResult> SendViaRelayAsync(...)`
- Add `IRouteConfirmationService` (Application) that:
  - Updates candidate stats
  - Upserts confirmed `PeerRoutingProfile`
- On successful send:
  - Direct => confirm using `UsedEndpoint`
  - Relay => confirm using relay host peer id

### J.6: DI + handler/service discovery (researched)

Concrete instruction (pre-made): place the new *application services* in `Percolator.Application` and the sqlite repository implementation + migrations in `Percolator.Infrastructure`.

- Add the new candidate repository + confirmation services in the **Percolator.Application** and **Percolator.Infrastructure** layers:
  - `Percolator.Network` interface for candidates: `IPeerRouteCandidateRepository`
  - `Percolator.Infrastructure` implementation: `SqlitePeerRouteCandidateRepository`

Phase 2 additions:

- `Percolator.Application/Network/IRouteConfirmationService.cs`
- `Percolator.Application/Network/RouteConfirmationService.cs`

Concrete registration anchors (pre-made):

- Wire application services in `Percolator.Application/Network/ServiceCollectionExtensions.cs` (inside `AddNetworkServices(...)`).
- Wire sqlite repository in `Percolator.Infrastructure/Cryptography/ServiceCollectionExtensions.cs` (it already registers sqlite-backed handshake/session repositories like `SqlitePendingSessionRepository` and `SqliteSentInvitationRepository`).
- `IPeerRoutingProfileRepository` is registered in `Percolator.Infrastructure/Network/ServiceCollectionExtensions.cs` as `SqlitePeerRoutingProfileRepository`.

### J.7: Ordering + failure semantics (Option B2)

Current behavior (researched) in `ApprovePendingSessionCommand.cs`:

- Creates the session and publishes `SecureSessionCreatedNotification`.
- For direct invites, it upserts routing (`AddGrpcEndPoint`) **before** attempting to deliver the `InviteHandshakeResponse`.

Concrete ordering (pre-made) for the refactor:

Inbound accept (`ApprovePendingSessionCommand`) (Phase 1):

1) After user acceptance is validated and session is created:
   - Persist a **candidate** route to `PeerRouteCandidates`.
2) Attempt delivery of the invite response (direct or relayed).
3) Delete the pending session record.

Failure semantics (pre-made):

- If candidate persistence fails, log and continue; do not fail acceptance solely due to candidate persistence.
- If delivery fails, return failure as today; candidate may already exist (acceptable; it reflects user-authorized provenance).

Outbound finalize (`InitiatorFinalizeService`):

1) On successful finalize with stable `peerIdentity`:
   - Persist candidate route derived from `SentInvitation`.
2) Phase 1: no promotion required.

- Determine route provenance:
  - For outbound sessions created via `InitiatorFinalize`:
    - Look up the matching outbound attempt by `PreHandshakeRecord.LocalRequestId` and/or `SentInvitation`.
    - If a `SentInvitation` exists and is `Relayed`, use `InviteRelayHostPeerId` as the relay route.
  - For inbound sessions created via explicit acceptance:
    - Use the accepted invitation’s callback endpoint (direct) OR relay host context (relayed) that is already present on the pending record.

Phase 2 note: confirmed profile upsert occurs only via `IRouteConfirmationService` (called from the chosen success boundary). Finalize should not upsert confirmed routes.

### J.8: Refactor plan (pre-made order)

Phase 1 (required):

**Dependency note:** Steps 1-4 are independent and can be done in any order (or in parallel). Step 5 is blocked by steps 1-4. Step 6 is blocked only by step 1. Step 7 is blocked by steps 5 and 6.

1) Add `PeerRouteCandidates` persistence:
   - Add `Percolator.Network` repo interface + sqlite implementation + EF migration.
2) Replace string route tokens with a discriminated union:
   - Add `PlannedRoute` in `Percolator.Network.Messaging`:
     - `PlannedRoute.Direct`
     - `PlannedRoute.Relay(PeerId RelayHostPeerId)`
   - Update `ISendExecutor.ExecuteAsync(...)` to accept `IReadOnlyList<PlannedRoute>` instead of `IReadOnlyList<string>`.
   - Update `SendOutcome`/`AttemptDetail` to keep human-readable strings for diagnostics, but stop parsing strings for behavior.
3) Extend `INetworkSender.SendAsync(...)` to include self identity (Option 2A):
   - `Task<SendOutcome> SendAsync(SelfId selfIdentityId, PeerId target, NetworkPayload payload, SendStrategy strategy, CancellationToken ct = default)`
   - Update all call sites accordingly.
4) Move orchestration implementations into Application (do now):
   - Move `Percolator.Network/Messaging/NetworkSender.cs` => `Percolator.Application/Network/Messaging/DefaultNetworkSender.cs`
   - Move `Percolator.Network/Messaging/SendExecutor.cs` => `Percolator.Application/Network/Messaging/DefaultSendExecutor.cs`
   - Keep `INetworkSender`, `ISendExecutor`, `PlannedRoute`, `SendOutcome`, etc. in `Percolator.Network`.
   - Update `Percolator.Application/Network/ServiceCollectionExtensions.cs` registrations to point at the new Application implementations.
5) Implement Approach 1 fallback (with the hard-coded sanity limits):
   - **Blocked by:** steps 1-4
   - In `DefaultNetworkSender.SendAsync(selfIdentityId, target, ...)`:
     - If confirmed profile planning yields no routes, query `IPeerRouteCandidateRepository.GetCandidatesAsync(selfIdentityId, target)`.
     - Apply the candidate policy:
       - Sort: `LastSuccessAtUtc desc` then `ObservedAtUtc desc`
       - Prefer relay-first if any relay candidates exist; otherwise direct-first
       - Limit to 3 candidates
     - Convert candidates into `PlannedRoute` list:
       - relay candidate => `PlannedRoute.Relay(relayHostPeerId)`
       - direct candidate => `PlannedRoute.Direct`
6) Ensure candidate writes exist for relayed handshakes:
   - **Blocked by:** step 1 (repository must exist)
   - Outbound relayed standard handshake finalize persists a **relay** candidate (`InviteRelayHostPeerId`) for the target peer.
   - Inbound accept persists relay candidate (`PendingSession.RelayHostPeerId`) for relayed invites.
7) Update tests (Phase 1):
   - **Blocked by:** steps 5 and 6 (implementation must exist)
   - Verify finalize persists relay candidate.
   - Verify sender can route a chat send via `PlannedRoute.Relay(...)` even when `PeerRoutingProfiles.GetByIdAsync(target)` returns null.

Phase 2 (defer):

8) Add `TransportSendResult` + endpoint attribution (Option A).
9) Add `IRouteConfirmationService` promotion + stats updates.
10) Add candidate pruning:
   - Define a retention policy (e.g., prune candidates with `LastSuccessAtUtc` null and `ObservedAtUtc` older than N days).

Concrete test locations (pre-made):

- `Percolator.ApplicationTests/Network/`:
  - Add `PeerRouteCandidateRepositoryTests.cs` (or infrastructure tests) to validate unique index semantics.
- Extend existing tests:
  - `Percolator.ApplicationTests/ReverseSignal/ApprovePendingSessionCommandTests.cs`
    - Verify candidate is persisted on acceptance.
  - `Percolator.ApplicationTests/Handshake/InitiatorFinalizeServiceTests.cs`
    - Verify finalize persists the candidate derived from SentInvitation.

Decision (pre-made): unit test promotion logic via `RouteConfirmationService` directly; do not rely on MediatR wiring.

Phase 2 tests (pre-made):

- `Percolator.ApplicationTests/Network/RouteConfirmationServiceTests.cs`:
  - Given a candidate direct endpoint, confirm promotes it into `PeerRoutingProfiles`.
  - Given a candidate relay host, confirm promotes relay link into `PeerRoutingProfiles`.

- Extend `Percolator.ApplicationTests/ReverseSignal/ApprovePendingSessionCommandTests.cs`:
  - Verify promotion occurs only when delivery succeeds.

### J.9: Remaining research / implementation questions (explicit)

- Migrations: `PeerRoutingProfiles` and its dependent tables are created in `Percolator.Infrastructure/Persistence/Migrations/20251228034329_InitialCreate.cs`.
  - Add the `PeerRouteCandidates` table via a new EF migration in the same `Percolator.Infrastructure.Persistence.Migrations` namespace/pattern.
- Confirm all `INetworkSender.SendAsync(...)` call sites can supply `SelfId` (the active identity).
- Confirm we have a single authoritative call site for chat sends (so the signature change is not overly invasive).
- Phase 2: promotion + pruning details.

### J.10: Key “gotchas” to verify during implementation

- Promotion correctness: ensure promotion runs exactly once per successful send (avoid double-confirmation from multiple layers).
- Candidate hygiene: ensure uniqueness constraint prevents candidate spam; consider pruning expired/very-old candidates.
- Endpoint attribution: ensure `TransportSendResult.UsedEndpoint` is non-null for direct sends that should confirm a candidate; if null, decide whether to skip promotion or to promote a less-specific "direct reachable" signal.

### J.11: Critical review notes (tradeoffs of Option B2)

- Option B2 provides clean semantics (confirmed routes only), but it is a larger change that introduces a second source of truth.
- First-message bootstrap is addressed by Approach 1 (fallback to candidates in `DefaultNetworkSender`).
- Promotion based on `DefaultSendExecutor` requires transport to surface the endpoint/relay used. Option A provides this via `TransportSendResult.UsedEndpoint`.

### J.12: Architecture notes (scratch-built vs current)

Scratch-built recommendation (pre-made): keep the Network project as *pure domain + ports* (routing models, route planning interfaces, transport ports). Keep orchestration in the Application layer.

- Today: `DefaultSendExecutor`/`DefaultNetworkSender` live in `Percolator.Network.Messaging` but are wired and used by Application services.
- Improvement direction (defer until after goal is met): move the orchestration implementations (sender/executor + promotion) into `Percolator.Application.Network` and keep `Percolator.Network` as contracts + domain types.

Laser-focus decision: do not add new MediatR routing handlers. Make routing explicit in the existing call paths.
