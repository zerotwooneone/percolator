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
- `DirectSessionId` — `readonly record struct(Guid Value)` in `Percolator.Network`
- `ConversationId` — `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `MessageId` — `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `ParticipantId` — `readonly record struct(Guid Value)` in `Percolator.Chat.ValueObjects`
- `PeerId` — `record(Guid Value)` in `Percolator.Network`
- `SelfId` — `readonly record struct(int Value)` in `Percolator.Identity`

**Key Identity Access:**
- `ActiveIdentityContext.Identity.SelfIdentityId` → `SelfId` (wraps `int`). Use `.Value` for raw int.
- `ISelfParticipantIdProvider.Get()` → `ParticipantId` (wraps `Guid`). Different from `SelfId`.

**What's Already Implemented:**
- `IConversationResolver.ResolveAsync(ConversationLookupKey, CancellationToken)` → `ConversationResolution(Conversation, int SelfIdentityId)` — the canonical way to turn a lookup key into a conversation
- `ConversationLookupKey.ForDirectSession(Guid)` — factory for direct session lookup
- `ChatReloadCoordinator` — Singleton, uses R3 `Subject`→`Debounce`→`SelectAwait` pipeline (missing `TimeProvider` injection)
- `PeerConnectionReloadCoordinator` — reference pattern: injects `TimeProvider`, passes to `Debounce(…, timeProvider)`
- `TimeProvider.System` registered as Singleton in `App.xaml.cs`
- `ChatStateService` — Singleton, owns `ObservableList<ChatMessageModel>` collections behind a `_stateGate` lock
- `SimulatedPeerModel.AddChatMessage` enforces max 50 messages
- `SendChatMessageToMainAsync` exists in `ISimulatorStateService`
- Peer cards rendered inline in `SimulatorPeersTabView.xaml` DataTemplate — there is **no** `SimulatedPeerCardView.xaml`
- All three Chat event publish sites (`PostTextMessageHandler`, `ReceiveTextMessageHandler`, `ReceiveDeliveredReceiptHandler`) have `request.LookupKey.DirectSessionId` available at publish time

**What's NOT Implemented:**
- WPF handler for `DeliveredReceiptReceivedEvent` — needed to update `ChatMessageModel.IsDelivered`
- `IsSending` property on `ChatMessageModel`
- Simulator chat UI (`SimulatorChatViewModel`) — does NOT exist
- Integration of chat UI into `SimulatedPeerCardViewModel`
- Initial chat load trigger — removed in Chunk C, not restored

**CRITICAL BUG: Session Key ≠ Conversation Key Mismatch**
`ChatViewModel.SetSession(sessionId)` receives `key.Value.ToString("N")` where `key.Value` is a `DirectSessionId` GUID. Messages are stored under this key. However, `ChatReloadCoordinator.ReloadCoreAsync` writes to `conversationId.Value.ToString("N")` — a **different GUID**. Messages written by reload go to a list nobody reads. See Step A.0.

**DESIGN PRINCIPLE: State Service model modification**
Per `r3.readme.md` §1, state services own observable collections of mutable models. Models can be modified directly on any thread — the ViewModel's job is to project them to the UI thread. We do NOT add "thread-safe wrapper methods" for individual property mutations. **Reactive properties already notify** (e.g., `BindableReactiveProperty<bool>` raises change events), so handlers can mutate model properties directly without an extra "state mutated" signal.

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
- `ChatStateService` keys its `ConcurrentDictionary` by `string`. Currently `ChatReloadCoordinator` writes via `conversationId.Value.ToString("N")`, but `ChatViewModel.SetSession` reads via `DirectSessionId.Value.ToString("N")` — **mismatch** (Step A.0)
- `ChatReloadCoordinator` — missing `TimeProvider` injection (should follow `PeerConnectionReloadCoordinator` pattern)
- `ChatMessageModel` — has `IsDelivered`, `IsRead` as `BindableReactiveProperty<bool>`. Missing `IsSending`.
- `ChatMessageSnapshot` — uses `string Id`. Should use `MessageId`.
- Issues: key mismatch, no initial load trigger, no delivery receipt handler, no sending state

**Simulator Chat:**
- `SimulatedPeerModel.RecentChatMessages` — `ObservableList<SimulatedChatMessageSnapshot>` with max 50
- `SimulatedPeerCardViewModel` — rendered inline in `SimulatorPeersTabView.xaml`. No chat section.
- Issue: No UI to display or send chat messages

### Step A.0: Fix Session Key ≠ Conversation Key Mismatch + Improve Domain Typing

**Problem:** `ChatViewModel.SetSession(sessionId)` stores messages under `DirectSessionId.ToString("N")`. `ChatReloadCoordinator.ReloadCoreAsync` writes to `ConversationId.Value.ToString("N")` — a different GUID. Messages never appear.

**Root cause:**
1. `SelectedChannelPaneViewModel.ResolveChatContent` → `key.Value` is a `DirectSessionId` GUID
2. `SessionScopeFactory.GetOrCreate(sessionId)` passes `key.Value.ToString("N")` as `string`
3. `ChatReloadCoordinator` writes under `ConversationId` key
4. Two different GUIDs → messages go to a list nobody reads

**Solution: Use `DirectSessionId` as the canonical storage key and enrich domain events.**

The `ConversationLookupKey` already carries an optional `DirectSessionId`. All three event publish sites (`PostTextMessageHandler`, `ReceiveTextMessageHandler`, `ReceiveDeliveredReceiptHandler`) have `request.LookupKey.DirectSessionId` available. Add `Guid? DirectSessionId` to the domain events so the WPF handlers can route without a reverse-lookup or DB call. This does NOT break domain isolation — `DirectSessionId` is already a concept in the Chat domain via `ConversationLookupKey`.

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
    // 5. Do NOT sort in the ViewModel — per wpf.readme.md §1, sorting is a XAML concern.
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

**File:** `Percolator.Chat/Events/TextMessagePostedEvent.cs` — add `DirectSessionIdValueObject? DirectSessionId` property

**File:** `Percolator.Chat/Events/TextMessageReceivedEvent.cs` — add `DirectSessionIdValueObject? DirectSessionId` property

**File:** `Percolator.Chat/Events/DeliveredReceiptReceivedEvent.cs` — add `DirectSessionIdValueObject? DirectSessionId` property

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

**File:** `Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs` — implement the two reload methods:

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
- `ChatMessageSnapshot.Id` is now `MessageId` (not `string`) — see Step A.3 for the type change
- `ParticipantId` supports `==` comparison (it's a `readonly record struct`)
- `ConversationResolution` is returned by `IConversationResolver.ResolveAsync` — it auto-creates the conversation if needed
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

### Step A.2: Handle DeliveredReceiptReceivedEvent — update model directly

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
        // IsSending is NOT overwritten — managed by optimistic insert + DeliveredReceiptReceivedEventHandler
    }

    public void Dispose() => _bag.Dispose();
}
```

**Note:** `ChatMessageViewModel` was introduced in Step A.0.2. The XAML bindings already use `.Value` for reactive properties (e.g., `IsDelivered.Value`), so no changes are needed to ChatView.xaml DataTemplate bindings.

**A.3.3: Update `ChatStateService.SyncMessages`**

`SyncMessages` already uses `existingById.TryGetValue` to merge — `UpdateFromSnapshot` doesn't overwrite `IsSending`, so it's naturally preserved. No changes needed to `SyncMessages` beyond the re-key from `string` to `DirectSessionId` done in A.0.1.

**A.3.4: Update `ChatStateUpdateHandlers` — optimistic insert via public method**

**Architectural decision:** Lock only around structural changes to ObservableList (adding/removing items). Do NOT lock around property updates on existing models — those are reactive and thread-safe. Encapsulate locking within ChatStateService via public methods.

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
- `DisposableBag _bag` — struct, no `new` needed (matches `SimulatedPeerCardViewModel`)
- `Messages` — concrete `NotifyCollectionChangedSynchronizedViewList<T>` (no interface variant exists)
- `Messages.Dispose()` explicitly in `DisposeCore` — per `wpf.readme.md` §3
- No `ViewMappings.xaml` entry — embedded inline, not resolved by implicit DataTemplate
- XAML binding: `{Binding MessageInput.Value, UpdateSourceTrigger=PropertyChanged}` — `.Value` required for `BindableReactiveProperty`

### Step A.5: Integrate simulator chat UI into SimulatedPeerCardViewModel

**Problem:** The simulator peer card needs to display the chat UI.

**Solution:** Add a `SimulatorChatViewModel` property to `SimulatedPeerCardViewModel` and embed the chat UI in the existing `SimulatorPeersTabView.xaml` DataTemplate. There is **no** `SimulatedPeerCardView.xaml` — peer cards are rendered inline.

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
- Binding uses `ChatViewModel.MessageInput.Value` (not `ChatViewModel.MessageInput`) — `.Value` is required for `BindableReactiveProperty`
- `SimulatedPeerCardViewModel` already injects `IUiDispatcher` as `_ui`, so passing it to `SimulatorChatViewModel` is straightforward
- `SimulatedPeerCardViewModel` already injects `ISimulatorStateService` as `_state`

### Step A.6: Verify simulator chat message persistence limits

**Verification:** `SimulatedPeerModel.AddChatMessage` enforces max 50 messages via `RemoveAt(0)` when count exceeds 50. `JsonSimulatorStateRepository` persists the `RecentChatMessages` list in the snapshot.

**No changes needed** — requirement 2b already satisfied.

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

## Chunk B

### Issue 1: Chat Messages Loaded from SQL Not Showing on Right Side (Locally Sent)

**Code Path:**

1. **Send Path:**
   - User clicks send in `ChatViewModel.SendCommand`
   - `ChatViewModel.SendCommand` → `Mediator.Send(PostTextMessageCommand)`
   - `PostTextMessageHandler.Handle`:
     - Gets `selfParticipantId` from `ActiveIdentityContext` (implements `ISelfParticipantIdProvider`)
     - `var selfParticipantId = ((ISelfParticipantIdProvider) _active).Get()` returns `new ParticipantId(Identity.Id)`
     - Calls `_writer.AddTextMessageAsync(resolution.Conversation.Id, resolution.SelfIdentityId, selfParticipantId, ...)`
   - `SqliteChatMessageWriter.AddTextMessageAsync`:
     - Persists to `MessageDbo` with `SenderId = senderId.Value` (Guid)
     - `SenderId` column stores the Guid value

2. **Load Path:**
   - `ChatReloadCoordinator.ReloadCoreAsync` or `ReloadFromSessionAsync`:
     - Calls `repo.GetByIdAsync(conversationId, selfIdentityId)`
   - `SqliteConversationRepository.GetByIdAsync`:
     - Loads `ConversationDbo` with included `Messages`
   - `ToDomain`:
     - Converts `MessageDbo` to `Message`: `new Message(new MessageId(m.MessageGuid), new ParticipantId(m.SenderId), ...)`
   - `ChatReloadCoordinator.SyncConversationToState`:
     - Gets `selfParticipantId = _selfParticipantIdProvider.Get()`
     - Creates `ChatMessageSnapshot` with `IsOwn: m.SenderId == selfParticipantId`

**Theory for Issue:**

The `ParticipantId` is a `readonly record struct(Guid Value)`, so equality comparison should work correctly. However, there are potential issues:

1. **Type Alias Confusion:** `ActiveIdentityContext` uses `using ChatParticipantId = Percolator.Chat.ValueObjects.ParticipantId;` and returns `new ChatParticipantId(Identity.Id)`. This should be equivalent to `ParticipantId` since it's an alias, but could indicate a design inconsistency.

2. **Self Participant Id Mismatch:** The `selfParticipantId` retrieved at load time (`_selfParticipantIdProvider.Get()`) might not match the `SenderId` stored in the database. This could happen if:
   - The active identity changed between send and load
   - The `ActiveIdentityContext.Identity.Id` is not the same as the participant ID used when sending

3. **Missing ConversationId Mapping:** The `DirectSessionId` to `ConversationId` mapping might be incorrect, causing messages to be loaded from the wrong conversation.

**Recommended Investigation:**
- Add logging to verify the actual `SenderId` values stored in SQL
- Verify `selfParticipantId` value at both send and load time
- Check if `DirectSessionId` → `ConversationId` mapping is correct

---

### Issue 2: Chat Messages Sent from Main to Direct Session Simulated Peer Not Showing in Simulator Chat

**Code Path:**

1. **Send Path (Main Window):**
   - User clicks send in `ChatViewModel.SendCommand`
   - `ChatViewModel.SendCommand` → `Mediator.Send(PostTextMessageCommand)`
   - `PostTextMessageHandler.Handle`:
     - Creates `ChatEnvelope` with `TextMessage`
     - Calls `_sender.SendChatEnvelopeToPeerAsync(chatEnvelope, route, ct)` for each participant
   - `RemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`:
     - Wraps in `InternalEnvelope`
     - Calls `_messageService.SendMessageAsync(internalEnvelope, recipient.PeerId, ct)`
   - `IMessageService.SendMessageAsync` → `GrpcMessageTransportService.SendMessageAsync`:
     - Calls `_simulatorOutbound.TryDeliverOpaqueMessage(endpoint, request, ct, out result)`
   - `SimulatorOutboundInterceptor.TryDeliverOpaqueMessage`:
     - Resolves simulated peer from endpoint (127.77.x.x IP range)
     - Calls `_state.ReceiveOpaqueMessageFromMainAsync(peerId, request, ct)`

2. **Receive Path (Simulator):**
   - `SimulatorStateService.ReceiveOpaqueMessageFromMainAsync`:
     - Decrypts message using available sessions
     - Parses `InternalEnvelope`
     - If `ChatEnvelope.TextMessage`:
       - Calls `model.AddChatMessage(isFromMain: true, content: text.Content, receivedUtc: ...)`
     - Returns `DeliverOpaqueMessageResponse { Version = 1 }`

**Theory for Issue:**

1. **Endpoint Resolution Failure:** The `TryResolveSimulatedPeerId` method in `SimulatorOutboundInterceptor` requires the endpoint to be in the 127.77.x.x IP range. If the routing profile for the simulated peer has a different endpoint, the interceptor will return `false` and the message won't be delivered to the simulator.

2. **Decryption Failure:** The message might not decrypt successfully:
   - No matching session found between main and the simulated peer
   - Session ratchet state mismatch
   - The decrypted plaintext is empty (line 613-616 returns early without adding to chat)

3. **Session Lookup Issue:** The code iterates through `model.SessionsMutable` to find a session that can decrypt. If the session was not properly established or is in a broken state, decryption will fail.

**Recommended Investigation:**
- Add logging in `SimulatorOutboundInterceptor.TryResolveSimulatedPeerId` to verify endpoint resolution
- Add logging in `SimulatorStateService.ReceiveOpaqueMessageFromMainAsync` to verify:
  - If the peer was found
  - If decryption succeeded
  - If the envelope was parsed as a chat message
- Verify that the session between main and the simulated peer is properly established

---

### Issue 3: Chat Messages Sent from Main to Relay Session Simulated Peer Not Enqueued in Relay Tab

**Code Path:**

1. **Send Path (Main Window):**
   - Same as Issue 2 up to `PostTextMessageHandler.Handle`
   - The message is sent via network to the relay host

2. **Simulator Send Path (Simulated Peer via Relay):**
   - When a simulated peer sends via relay (e.g., `SendChatMessageToMainAsync`):
     - `SimulatorStateService.SendChatMessageToMainAsync`:
       - Checks `model.ConnectionMode.CurrentValue == ConnectionMode.ViaRelay`
       - Gets `relayHostPeerId = model.RelayPeerId.CurrentValue`
       - Calls `EnqueueRelayUpstreamToMainAsync(relayHostPeerId, cipher.Value, debugType: "Chat", ct)`
   - `SimulatorStateService.EnqueueRelayUpstreamToMainAsync`:
     - Adds message to relay queue: `_relays[relayHostPeerId].UpstreamQueue.Add(...)`
     - Triggers save

3. **Main → Relay Path:**
   - When main sends to a peer that is connected via relay:
   - The message should be routed through the relay host
   - The relay host should receive it and forward it to the target peer

**Theory for Issue:**

1. **Missing Main → Relay Routing:** There appears to be no code path for messages sent FROM main TO a relay-connected peer that enqueues them in the simulator's relay tab. The `EnqueueRelayUpstreamToMainAsync` is only called from:
   - `SimulatedPeerModel` sending to main (simulator → main)
   - `SimulatorRelayDeliveryService` handling relayed messages from main
   - Handshake-related relay operations

2. **Relay Delivery Service Gap:** The `SimulatorRelayDeliveryService` handles relayed messages, but it may not be intercepting chat messages sent from main to relay-connected peers. The service might only be handling handshake messages, not chat messages.

3. **Connection Mode Not Respected:** When main sends a message to a peer, the routing logic might not check if that peer is connected via relay. The `PostTextMessageHandler` sends to all participants without considering their connection mode.

**Recommended Investigation:**
- Verify if `SimulatorRelayDeliveryService` is supposed to intercept chat messages from main
- Check if there's a missing interceptor or handler for main → relay peer chat messages
- Verify that the routing logic in `PostTextMessageHandler` or `RemoteEnvelopeSender` considers relay connections
- Add logging to verify where messages sent from main to relay-connected peers actually go

---

### Summary of Theories

| Issue | Root Cause Theory | Evidence |
|-------|-------------------|----------|
| **1. SQL load wrong side** | Self participant ID mismatch between send and load | Type alias confusion, potential identity change |
| **2. Direct session simulator** | Endpoint resolution or decryption failure | Requires 127.77.x.x IP, session lookup might fail |
| **3. Relay session simulator** | Missing main → relay routing path | No code path found for main → relay peer chat messages |

---

## Chunk C

### Issue 3: Chat Messages Sent from Main to Relay Session Simulated Peer Not Enqueued in Relay Tab

**Root Cause:** Missing Main → Relay Routing Path

When main sends a chat message to a peer that is connected via relay, the message is sent directly to the peer's endpoint without checking the peer's connection mode. The simulator's relay tab only shows messages when they are explicitly enqueued via `EnqueueRelayDownstreamToPeerAsync`. There is no interceptor that detects when main is sending to a relay-connected peer and enqueues the message in the simulator's relay queue.

**Current Code Path Gap:**

1. **Send Path (Main Window):**
   - `ChatViewModel.SendCommand` → `Mediator.Send(PostTextMessageCommand)`
   - `PostTextMessageHandler.Handle`:
     - Creates `ChatEnvelope` with `TextMessage`
     - Calls `_sender.SendChatEnvelopeToPeerAsync(chatEnvelope, route, ct)` for each participant
   - `RemoteEnvelopeSender.SendChatEnvelopeToPeerAsync`:
     - Wraps in `InternalEnvelope`
     - Calls `_messageService.SendMessageAsync(internalEnvelope, recipient.PeerId, ct)`
   - `MessageService.SendMessageAsync` (in `Percolator.Application\Network\MessageService.cs`):
     - Encrypts the envelope to a session cipher
     - Calls `_networkSender.SendAsync` with `SendStrategy.DirectThenRelay`
     - **NO CHECK** for whether the peer is connected via relay in the simulator

2. **Simulator Relay Tab (What Should Happen):**
   - When main sends to a relay-connected peer, the message should:
     - Be intercepted before network send
     - Be enqueued in the simulator's relay queue via `EnqueueRelayDownstreamToPeerAsync` (which calls `SimulatedRelayModel.EnqueueMessage`)
     - Appear in the relay tab UI as a downstream message
     - Be forwarded to the target peer by the relay host

**Solution: Extend ISimulatorOutboundInterceptor**

The solution is to extend `ISimulatorOutboundInterceptor` with a new method that checks if the recipient peer is a simulated peer connected via relay. If so, enqueue the message in the simulator's relay queue using the same pattern as relayed handshakes.

### Step C.1: Add Method to ISimulatorOutboundInterceptor

**Problem:** The existing `ISimulatorOutboundInterceptor` only intercepts gRPC calls based on endpoint resolution. We need a method that can be called from `MessageService` to intercept message sends based on the **recipient identity PKH** (crypto identifier), because `PeerId` is not a stable identifier across peers.

**Solution:** Add a new method to `ISimulatorOutboundInterceptor` that checks if a message to a peer should be routed via simulator relay based on **recipientPublicKeyHash (PKH)**.

**File:** `Percolator.Application/Network/ISimulatorOutboundInterceptor.cs`

**Current State:** This method exists, but it currently takes `PeerId recipientPeerId`. That is incorrect for simulator relay routing.

**Change the method signature to (after the existing methods):**

```csharp
/// <summary>
/// Checks if a message to the specified peer should be routed via simulator relay.
/// If the peer is a simulated peer connected via relay, enqueues the message in the simulator's
/// relay queue and returns true. Otherwise, returns false to proceed with normal network send.
/// </summary>
/// <param name="recipientPublicKeyHash">The recipient identity PKH (32 bytes)</param>
/// <param name="cipherBytes">The encrypted message payload (session cipher)</param>
/// <param name="debugType">Optional debug type for diagnostics</param>
/// <param name="cancellationToken">Cancellation token</param>
/// <returns>True if the message was enqueued in simulator relay (skip network send), false otherwise</returns>
Task<bool> TryRouteMessageViaSimulatorRelayAsync(
    byte[] recipientPublicKeyHash,
    byte[] cipherBytes,
    string? debugType = null,
    CancellationToken cancellationToken = default);
```

### Step C.2: Implement the Method in SimulatorOutboundInterceptor

**Problem:** Need to implement the new method to check if the recipient is a relay-connected simulated peer and enqueue the message using the same pattern as relayed handshakes.

**Solution:** Implement the method following the pattern used by relayed handshakes (see `SimulatorStateService.cs` calls to `EnqueueRelayDownstreamToPeerAsync`).

**File:** `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`

**Current State:** `ISimulatorDiagnosticsService _diagnostics` is already injected and stored.

**Important Correction:** Remove the current PeerId-based lookup and PKH computation. This method must:
1) Use `recipientPublicKeyHash` as the authoritative recipient identifier
2) Resolve whether that PKH belongs to a simulated peer using `_state.TryGetPeerIdByIdentityPkhAsync(recipientPublicKeyHash, ct)`
3) If simulated and `ConnectionMode.ViaRelay`, enqueue using `EnqueueRelayDownstreamToPeerAsync(relayHostPeerId, recipientPublicKeyHash, cipherBytes, ...)`

**Update the existing method to:**

```csharp
public async Task<bool> TryRouteMessageViaSimulatorRelayAsync(
    byte[] recipientPublicKeyHash,
    byte[] cipherBytes,
    string? debugType = null,
    CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    if (recipientPublicKeyHash is null) throw new ArgumentNullException(nameof(recipientPublicKeyHash));
    if (recipientPublicKeyHash.Length != 32) return false;
    if (cipherBytes is null) throw new ArgumentNullException(nameof(cipherBytes));

    // Resolve simulated peer by PKH (do NOT assume PeerId is meaningful across peers)
    var simulatedPeerId = await _state
        .TryGetPeerIdByIdentityPkhAsync(recipientPublicKeyHash, cancellationToken)
        .ConfigureAwait(false);
    if (simulatedPeerId is null) return false;

    var peer = _state.Peers.FirstOrDefault(p => p.PeerId == simulatedPeerId.Value);
    if (peer is null) return false;

    // Check if peer is connected via relay
    if (peer.ConnectionMode.CurrentValue != ConnectionMode.ViaRelay)
    {
        return false; // Not connected via relay, proceed with normal send
    }

    // Get the relay host peer ID
    if (peer.RelayPeerId.CurrentValue.Value == Guid.Empty)
    {
        _logger.LogWarning("[simulator relay] Peer {PeerId} has ViaRelay mode but no relay host ID", peer.PeerId);
        return false; // Fallback to normal send
    }

    var relayHostPeerId = peer.RelayPeerId.CurrentValue;

    // Enqueue in simulator's relay queue (downstream: main → target peer via relay)
    await _state.EnqueueRelayDownstreamToPeerAsync(
        relayHostPeerId: relayHostPeerId,
        targetPkh: recipientPublicKeyHash,
        opaqueBytes: cipherBytes,
        debugType: debugType ?? "ChatMessage",
        cancellationToken: cancellationToken)
        .ConfigureAwait(false);

    _logger.LogInformation(
        "[simulator relay] Routed message to peer {PeerId} via relay host {RelayHost}",
        peer.PeerId,
        relayHostPeerId);

    _diagnostics.Emit(
        SimulatorDiagnosticEventType.RelayEnqueued,
        $"Message routed to peer {peer.PeerId} via simulator relay host {relayHostPeerId}",
        peerId: peer.PeerId,
        relayHostPeerId: relayHostPeerId);

    return true; // Message enqueued, skip network send
}
```

### Step C.3: Wire Interceptor into MessageService

**Problem:** The `MessageService` needs to call the simulator relay interceptor before sending via the network.

**Solution:** Inject `ISimulatorOutboundInterceptor` into `MessageService` and call the new method in `SendMessageAsync` / `SendMessageWithResponseAsync`.

**Important Correction:** The interceptor requires **recipient PKH**.

**Updated approach:** Do **not** plumb PKH through `IMessageService` or the outbound send call sites. Instead, resolve PKH inside `MessageService` using the existing persisted mapping in `IPeerPublicSigningKeyStore`.

This keeps call sites stable (they only know `PeerId`) while still enabling simulator relay routing via PKH.

**File:** `Percolator.Application/Network/MessageService.cs`

**Add this private field (after the existing private fields, after `private readonly IOutboundMessageWireTap _wireTap;`):**

```csharp
private readonly ISimulatorOutboundInterceptor? _simulatorOutboundInterceptor;
```

**Replace the existing constructor (the `public MessageService` constructor) with:**

```csharp
public MessageService(
    ILogger<MessageService> logger,
    IDirectSessionRepository sessions,
    ISecureMessagingService secureMessaging,
    ActiveIdentityContext active,
    INetworkSender networkSender,
    IOutboundMessageWireTap wireTap,
    ISimulatorOutboundInterceptor? simulatorOutboundInterceptor = null)
// Optional for non-simulator scenarios
{
    _logger = logger;
    _sessions = sessions;
    _secureMessaging = secureMessaging;
    _active = active;
    _networkSender = networkSender;
    _wireTap = wireTap;
    _simulatorOutboundInterceptor = simulatorOutboundInterceptor;
}
```

**Current State:** `MessageService` calls the interceptor with PKH after resolving it via `IPeerPublicSigningKeyStore.GetPublicKeyHashByPeerIdAsync`.

**Files:**
- `Percolator.Application/Network/MessageService.cs`
- `Percolator.Identity/IPeerPublicSigningKeyStore.cs` (already exists)

**Required change (high level):**
1) Inject `IPeerPublicSigningKeyStore` into `MessageService`.
2) Before attempting simulator relay routing:
   - `var recipientPkh = await _keyStore.GetPublicKeyHashByPeerIdAsync(recipientPeerId, ct)`
   - if non-null, call `_simulatorOutboundInterceptor.TryRouteMessageViaSimulatorRelayAsync(recipientPkh, cipher.Value, ...)`
3) Leave `IMessageService` signature unchanged.

### Step C.4: Register ISimulatorOutboundInterceptor in DI

**Problem:** The `ISimulatorOutboundInterceptor` is already registered in Desktop.Wpf, but we need to ensure `MessageService` can receive it as a dependency. Also need to verify `ISimulatorDiagnosticsService` is registered for the updated constructor.

**Solution:** The interceptor is already registered in `Desktop.Wpf/App.xaml.cs` at line 179. No changes needed for registration. The DI container will automatically inject it into `MessageService` when registered in the Application layer.

**Verification:** Verify that `ISimulatorDiagnosticsService` is registered in Desktop.Wpf DI. If not, add it.

**File:** `Desktop.Wpf/App.xaml.cs`

**Check if this registration exists (should be in the service registration section):**

```csharp
services.AddSingleton<ISimulatorDiagnosticsService, SimulatorDiagnosticsService>();
```

**If the above line does not exist, add it to the service registration section.**

**File:** `Percolator.Application/Network/ServiceCollectionExtensions.cs`

**No changes needed** - the `ISimulatorOutboundInterceptor` is registered in Desktop.Wpf and will be available to MessageService through the DI container.

### Step C.5: Remove Incorrect PeerId-Based Simulator Relay Routing (Code Already Implemented)

**Problem:** The current code uses `PeerId` as the basis for determining whether a message is destined for a simulated relay-connected peer. This is incorrect because `PeerId` is not meaningful across peers.

**Required Cleanup (must be done as part of this chunk):**

- Update `ISimulatorOutboundInterceptor.TryRouteMessageViaSimulatorRelayAsync` signature from `(PeerId recipientPeerId, ...)` to `(byte[] recipientPublicKeyHash, ...)`.
- Update `SimulatorOutboundInterceptor.TryRouteMessageViaSimulatorRelayAsync` implementation to:
  - Stop using `_state.ComputePublicKeyHashAsync(recipientPeerId, ...)`
  - Stop searching `_state.Peers` by `recipientPeerId`
  - Resolve simulated peer by PKH via `_state.TryGetPeerIdByIdentityPkhAsync(recipientPublicKeyHash, ct)`
  - Enqueue using `targetPkh: recipientPublicKeyHash`.
- Update the main-window send path to resolve PKH via `IPeerPublicSigningKeyStore.GetPublicKeyHashByPeerIdAsync` before attempting simulator relay routing.

**Update / fix unit tests** added for the initial implementation:
- Desktop.Wpf.Tests: adjust tests to pass PKH and mock `TryGetPeerIdByIdentityPkhAsync` instead of `ComputePublicKeyHashAsync`.
- Percolator.ApplicationTests: adjust `MessageService_SendMessageAsync_WhenInterceptorRoutesViaSimulatorRelay...` to mock `IPeerPublicSigningKeyStore.GetPublicKeyHashByPeerIdAsync` and call the existing `SendMessageAsync(envelope, recipientPeerId, ct)`.

### Verification

**Manual Test Steps:**

1. Start the application with simulator
2. Create two simulated peers (Peer A and Peer B)
3. Make Peer A a relay host
4. Establish a relay session between main and Peer B (via Peer A as relay host)
5. Send a chat message from main to Peer B
6. Verify:
   - The message appears in the simulator's relay tab (under Peer A's relay queue, in the "Downstream to Peers" section)
   - The message shows the target peer's PKH
   - The message can be manually delivered from the relay tab
   - Peer B receives the message in its chat UI

**Expected Result:** Messages sent from main to relay-connected simulated peers now appear in the simulator's relay tab as downstream messages and can be manually delivered to the target peer.

---

## Chunk D

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

- `public sealed record IdentityPublicKeyHash(byte[] Value) : Percolator.Identity.Primitives.ByteArrayRecord(Value)`
- Enforce invariant in factory method(s): 32 bytes (SHA-256) only.
- Provide explicit construction and boundary conversions:
  - `static IdentityPublicKeyHash FromBytes(byte[] value)` (validates non-null and `Length == 32`)
  - `static IdentityPublicKeyHash FromSpki(byte[] publicKeySpki)` (computes `SHA256.HashData(...)` and validates)
  - `byte[] ToArray()` (returns a copy to avoid callers mutating the underlying `Value`)

Note: do not expose a public constructor without validation unless you are comfortable relying on call site discipline.

### Work items

**D.1: Add the new PKH type**

- Add `Percolator.Identity/IdentityPublicKeyHash.cs`
- Implement:
  - `FromBytes(byte[])` validation (`ArgumentNullException`, `ArgumentException` for non-32)
  - Optional: `FromSpki(byte[])` (uses `System.Security.Cryptography.SHA256.HashData`)
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

- Wire tap
- Network send

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

## Chunk G

### Goal

Perform a targeted rename pass for PKH-related identifiers so the codebase consistently uses **`IdentityPublicKeyHash`** as the canonical name.

This chunk is **TODO-only**: you will perform the renames.

### Rename conventions to apply

- Type name: `IdentityPublicKeyHash`
- Parameter/local name: `identityPublicKeyHash`
- Avoid abbreviations like `pkh` in public APIs. (Private locals are OK if you prefer, but this scan assumes you want to remove them for clarity.)

### TODO checklist (repo-specific)

#### G.1 Simulator (Desktop.Wpf) API + model naming

- [ ] `Desktop.Wpf/Features/Simulator/ISimulatorStateService.cs`
  - Rename method:
    - `TryGetPeerIdByIdentityPkhAsync(byte[] recipientPublicKeyHash, ...)`
    - To: `TryGetPeerIdByIdentityPublicKeyHashAsync(byte[] identityPublicKeyHash, ...)`
  - Rename parameters:
    - `recipientPublicKeyHash` -> `identityPublicKeyHash`
    - `targetPkh` -> `targetIdentityPublicKeyHash`

- [ ] `Desktop.Wpf/Features/Simulator/SimulatorOutboundInterceptor.cs`
  - Rename parameter:
    - `recipientPublicKeyHash` -> `identityPublicKeyHash`
  - Rename call site:
    - `_state.TryGetPeerIdByIdentityPkhAsync(...)` -> `_state.TryGetPeerIdByIdentityPublicKeyHashAsync(...)`
  - Rename enqueue parameter usage:
    - `targetPkh: recipientPublicKeyHash` -> `targetIdentityPublicKeyHash: identityPublicKeyHash`

- [ ] `Desktop.Wpf/Features/Simulator/Models/SimulatedRelayModel.cs`
  - Rename `InboundRelayMessage.TargetPkh` -> `TargetIdentityPublicKeyHash`
  - Update error text accordingly (if you want consistency).
  - Update snapshot mapping fields that reference `TargetPkh`.

- [ ] `Desktop.Wpf/Features/Simulator/JsonSimulatorStateRepository.cs`
  - Rename DTO fields/locals:
    - `TargetPkh` -> `TargetIdentityPublicKeyHash`
    - `EnsureIdentityPublicKeyHash(...)` is already aligned and should remain.

- [ ] `Desktop.Wpf/Features/Simulator/PeerStateSnapshot.cs`
  - Rename snapshot properties:
    - `TargetPublicKeyHash` -> `TargetIdentityPublicKeyHash`
    - `PendingStandardHandshakeToMainResponderPublicKeyHash` -> `PendingStandardHandshakeToMainResponderIdentityPublicKeyHash`
    - `PublishedPreKeyBundleSnapshot.RecipientPublicKeyHash` -> `RecipientIdentityPublicKeyHash`

#### G.2 Simulator tests

- [ ] `Desktop.Wpf.Tests/SimulatorOutboundInterceptorRelayRoutingTests.cs`
  - Rename local vars:
    - `recipientPublicKeyHash` -> `identityPublicKeyHash`
  - Update mock:
    - `TryGetPeerIdByIdentityPkhAsync` -> `TryGetPeerIdByIdentityPublicKeyHashAsync`

#### G.3 Application / CLI naming

- [ ] `Percolator.Application/Cli/RequestPreKeyBundleByPkhHandler.cs`
  - Decide whether you want to rename this command/handler to remove `ByPkh`.
  - If yes, suggested naming:
    - `RequestPreKeyBundleByIdentityPublicKeyHash*`
    - Replace variables:
      - `expectedRemotePkh` -> `expectedRemoteIdentityPublicKeyHash`
      - `remotePkh` -> `remoteIdentityPublicKeyHash`
      - `initiatorPkh` -> `initiatorIdentityPublicKeyHash` (if present in other files)

#### G.4 Application / Network route models

- [ ] `Percolator.Application/Network/IRemoteEnvelopeSender.cs`
  - Rename record field:
    - `RecipientRoute(PeerId PeerId, byte[]? PublicKeyHash)`
    - To: `RecipientRoute(PeerId PeerId, byte[]? IdentityPublicKeyHash)`
  - Update call sites accordingly.

#### G.5 Chat adapter naming (optional; depends on how strongly you want to purge “Pkh”)

These are intentionally named `Pkh` today to avoid Chat referencing Identity. You can still rename them for clarity if you’re willing to accept more verbose names.

- [ ] `Percolator.Chat/App/IPkhPeerResolver.cs`
  - Consider renaming:
    - `IPkhPeerResolver` -> `IIdentityPublicKeyHashPeerResolver`
    - `GetParticipantIdByPkhAsync(Pkh pkh, ...)` -> `GetParticipantIdByIdentityPublicKeyHashAsync(Pkh identityPublicKeyHash, ...)`

- [ ] `Percolator.Application/Apps/Chat/PkhPeerResolver.cs`
  - Rename class + method accordingly if you do G.5.

### Notes / guardrails

- Keep this rename work **after** Chunk D if you introduce a real `IdentityPublicKeyHash` type; otherwise you’ll be renaming `byte[]`-based APIs twice.
- Expect ripples into JSON persistence DTOs and UI view models for simulator state; those are worth renaming together to avoid half-old/half-new terminology.