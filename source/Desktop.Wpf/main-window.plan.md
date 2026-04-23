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

## Chunk G

### Goal

Identify and eliminate unnecessary `IdentityPublicKeyHash.ToArray()` allocations by passing the strongly-typed value directly through the call chain where possible. This reduces memory allocations and improves type safety.

### Current State Analysis

Following the refactor to use `IdentityPublicKeyHash` instead of `byte[]` in `IPeerPublicSigningKeyStore`, several call sites still convert to byte arrays unnecessarily. This creates allocation overhead and breaks the strongly-typed boundary.

### Problem Callsites (IdentityPublicKeyHash.ToArray())

**G.1: Application layer - RecipientRoute uses byte[]? PublicKeyHash (HIGH IMPACT)**

The `RecipientRoute` record in `IRemoteEnvelopeSender.cs` uses `byte[]? PublicKeyHash` instead of `IdentityPublicKeyHash?`. This is a cross-cutting issue affecting the entire Application layer chat dispatch system.

- `Percolator.Application\Network\IRemoteEnvelopeSender.cs` (line 11)
  - `public sealed record RecipientRoute(PeerId PeerId, byte[]? PublicKeyHash);`
  - **Impact:** All handlers creating RecipientRoute must call `ToArray()`

**G.1.1: Dispatch handlers calling pkh?.ToArray()**

All these handlers resolve PKH from the key store, then immediately convert to byte[]:

- `Percolator.Application\Apps\Chat\Handlers\DispatchTextMessageHandler.cs` (line 66)
  - `var route = new RecipientRoute(recipientId, pkh?.ToArray());`

- `Percolator.Application\Apps\Chat\Handlers\DispatchDeliveredReceiptHandler.cs` (line 56)
  - `await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);`

- `Percolator.Application\Apps\Chat\Handlers\DispatchEmojiAnnotationHandler.cs` (line 57)
  - `await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);`

- `Percolator.Application\Apps\Chat\Handlers\DispatchReadReceiptHandler.cs` (line 60)
  - `await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);`

- `Percolator.Application\Apps\Chat\Handlers\DispatchSignedAdminCommitOperationHandler.cs` (line 64)
  - `await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);`

- `Percolator.Application\Apps\Chat\AdminOperationDispatcher.cs` (line 156)
  - `await _sender.SendChatEnvelopeToPeerAsync(envelope.ChatEnvelope, new RecipientRoute(pid, pkh?.ToArray()), ct);`

**G.1.2: Commands passing byte[] directly**

- `Percolator.Application\Apps\Chat\CreateGroupFromIdentityKeysCommand.cs` (line 137)
  - `var route = new RecipientRoute(pid, pkh);` where pkh is byte[]

- `Percolator.Application\Apps\Chat\KeyVersionAdoptedHandler.cs` (line 71)
  - `var route = new RecipientRoute(new Percolator.Identity.PeerId(adminPeerId.Value), pkh);` where pkh is byte[]

**G.1.3: Test dictionary key using ToArray()**

- `Percolator.ApplicationTests\Apps\Chat\CreateGroupTests.cs` (line 63)
  - `_map.TryGetValue(Convert.ToBase64String(publicKeyHash.ToArray()), out var id);`
  - Uses ToArray() to create Base64 string key

**G.2: Simulator relay routing (Desktop.Wpf - production code)**

- `Desktop.Wpf\Features\Simulator\SimulatorOutboundInterceptor.cs` (line 208)
  - Calls `recipientPublicKeyHash.ToArray()` when passing to `_state.EnqueueRelayDownstreamToPeerAsync`
  - **Opportunity:** Add typed overload to `ISimulatorStateService.EnqueueRelayDownstreamToPeerAsync` accepting `IdentityPublicKeyHash`

- `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs` (line 2242)
  - Current typed overload delegates to byte[] overload: `return TryGetPeerIdByIdentityPublicKeyHashAsync(recipientPublicKeyHash.ToArray(), cancellationToken)`
  - **Opportunity:** Make typed overload the primary implementation, remove byte[] delegation

**G.3: UI snapshotting (Desktop.Wpf - ViewModel/Model code)**

- `Desktop.Wpf\Features\Simulator\SimulatedRelayQueuePanelViewModel.cs` (lines 89, 266)
  - Calls `inbound.TargetPkh.ToArray()` when creating view models and snapshots
  - **Opportunity:** View models should hold `IdentityPublicKeyHash` directly, convert only at persistence boundaries

- `Desktop.Wpf\Features\Simulator\Models\SimulatedRelayModel.cs` (line 76)
  - Calls `x.TargetPkh.ToArray()` when creating `InboundRelayMessageSnapshot`
  - **Opportunity:** Snapshot record should use `IdentityPublicKeyHash`, convert only for JSON/protobuf serialization

**G.4: Test mock setups (test projects)**

- `Desktop.Wpf.Tests\SimulatorRelayDeliveryServiceTests.cs` (line 180)
  - Mock uses `pkh.ToArray().SequenceEqual(initiatorPkh)` for matching
  - **Opportunity:** Use `It.Is<IdentityPublicKeyHash>(pkh => pkh.Equals(expectedPkh))` instead

- `Percolator.ApplicationTests\Network\NetworkTransportPortAdapterTests.cs` (line 119)
  - Mock returns `IdentityPublicKeyHash.FromBytes(new byte[32]...).ToArray()` - double conversion
  - **Opportunity:** Return `IdentityPublicKeyHash` directly

- `Percolator.ApplicationTests\Cli\RequestPreKeyBundleByPkhHandlerTests.cs` (lines 76, 270, 322)
  - Mock uses `h.ToArray().SequenceEqual(expectedPkh)` for matching
  - **Opportunity:** Use `It.Is<IdentityPublicKeyHash>(h => h.Equals(expectedPkh))` instead

- `Percolator.ApplicationTests\Apps\Chat\CreateGroupConversationHandlerTests.cs` (lines 40, 42, 44)
  - Mock uses `h.ToArray().SequenceEqual(alicePkh)` for matching
  - **Opportunity:** Use `It.Is<IdentityPublicKeyHash>(h => h.Equals(expectedPkh))` instead

### G.1: Fix RecipientRoute to use IdentityPublicKeyHash

**File:** `Percolator.Application\Network\IRemoteEnvelopeSender.cs`

Update RecipientRoute to use typed value:
```csharp
public sealed record RecipientRoute(PeerId PeerId, Percolator.Identity.IdentityPublicKeyHash? PublicKeyHash);
```

**Files to update - Dispatch handlers (6 files):**

1. `Percolator.Application\Apps\Chat\Handlers\DispatchTextMessageHandler.cs` (line 66)
   ```csharp
   // Before
   var route = new RecipientRoute(recipientId, pkh?.ToArray());
   // After
   var route = new RecipientRoute(recipientId, pkh);
   ```

2. `Percolator.Application\Apps\Chat\Handlers\DispatchDeliveredReceiptHandler.cs` (line 56)
   ```csharp
   // Before
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);
   // After
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken);
   ```

3. `Percolator.Application\Apps\Chat\Handlers\DispatchEmojiAnnotationHandler.cs` (line 57)
   ```csharp
   // Before
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);
   // After
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken);
   ```

4. `Percolator.Application\Apps\Chat\Handlers\DispatchReadReceiptHandler.cs` (line 60)
   ```csharp
   // Before
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);
   // After
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken);
   ```

5. `Percolator.Application\Apps\Chat\Handlers\DispatchSignedAdminCommitOperationHandler.cs` (line 64)
   ```csharp
   // Before
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh?.ToArray()), cancellationToken);
   // After
   await _sender.SendChatEnvelopeToPeerAsync(chat, new RecipientRoute(recipientId, pkh), cancellationToken);
   ```

6. `Percolator.Application\Apps\Chat\AdminOperationDispatcher.cs` (line 156)
   ```csharp
   // Before
   await _sender.SendChatEnvelopeToPeerAsync(envelope.ChatEnvelope, new RecipientRoute(pid, pkh?.ToArray()), ct);
   // After
   await _sender.SendChatEnvelopeToPeerAsync(envelope.ChatEnvelope, new RecipientRoute(pid, pkh), ct);
   ```

**Files to update - Commands (2 files):**

7. `Percolator.Application\Apps\Chat\CreateGroupFromIdentityKeysCommand.cs` (line 137)
   ```csharp
   // Before
   var route = new RecipientRoute(pid, pkh);
   // After
   var route = new RecipientRoute(pid, IdentityPublicKeyHash.FromBytes(pkh));
   ```

8. `Percolator.Application\Apps\Chat\KeyVersionAdoptedHandler.cs` (line 71)
   ```csharp
   // Before
   var route = new RecipientRoute(new Percolator.Identity.PeerId(adminPeerId.Value), pkh);
   // After
   var route = new RecipientRoute(new Percolator.Identity.PeerId(adminPeerId.Value), IdentityPublicKeyHash.FromBytes(pkh));
   ```

**File to update - Test:**

9. `Percolator.ApplicationTests\Apps\Chat\CreateGroupTests.cs` (line 63)
   ```csharp
   // Before
   _map.TryGetValue(Convert.ToBase64String(publicKeyHash.ToArray()), out var id);
   // After
   _map.TryGetValue(Convert.ToBase64String(publicKeyHash.AsReadOnlyMemory().ToArray()), out var id);
   ```

### G.2: Add typed overload to ISimulatorStateService

**File:** `Desktop.Wpf\Features\Simulator\ISimulatorStateService.cs`

Add typed overload for `EnqueueRelayDownstreamToPeerAsync`:
```csharp
Task EnqueueRelayDownstreamToPeerAsync(
    Percolator.Network.PeerId relayHostPeerId,
    Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
    byte[] opaqueBytes,
    string? debugType = null,
    CancellationToken cancellationToken = default);
```

**File:** `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs`

Implement typed overload as primary, keep byte[] as convenience:
```csharp
public async Task EnqueueRelayDownstreamToPeerAsync(
    Percolator.Network.PeerId relayHostPeerId,
    Percolator.Identity.IdentityPublicKeyHash targetIdentityPublicKeyHash,
    byte[] opaqueBytes,
    string? debugType = null,
    CancellationToken cancellationToken = default)
{
    // Implementation using typed value directly
    // No ToArray() call needed
    await EnqueueRelayDownstreamToPeerAsync(
        relayHostPeerId,
        targetIdentityPublicKeyHash.ToArray(),
        opaqueBytes,
        debugType,
        cancellationToken);
}

// Existing byte[] overload remains for backward compatibility
public async Task EnqueueRelayDownstreamToPeerAsync(
    Percolator.Network.PeerId relayHostPeerId,
    byte[] targetIdentityPublicKeyHash,
    byte[] opaqueBytes,
    string? debugType = null,
    CancellationToken cancellationToken = default)
{
    // Existing implementation unchanged
}
```

**File:** `Desktop.Wpf\Features\Simulator\SimulatorOutboundInterceptor.cs`

Update to use typed overload:
```csharp
await _state.EnqueueRelayDownstreamToPeerAsync(
    relayHostPeerId: relayHostPeerId,
    targetIdentityPublicKeyHash: recipientPublicKeyHash, // No ToArray()
    opaqueBytes: cipherBytes,
    debugType: debugType ?? "ChatMessage",
    cancellationToken: cancellationToken)
```

### G.3: Update TryGetPeerIdByIdentityPublicKeyHashAsync to use typed implementation

**File:** `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs`

Make typed overload the primary implementation:
```csharp
// Typed overload - primary implementation
public async Task<Percolator.Network.PeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(Percolator.Identity.IdentityPublicKeyHash recipientPublicKeyHash, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();
    await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        // Direct lookup using typed value
        foreach (var peer in _peers)
        {
            var peerPkh = await ComputePublicKeyHashAsync(peer.PeerId, cancellationToken).ConfigureAwait(false);
            if (peerPkh.Equals(recipientPublicKeyHash))
                return peer.PeerId;
        }
        return null;
    }
    finally
    {
        _stateGate.Release();
    }
}

// Byte[] overload - convenience wrapper
public async Task<Percolator.Network.PeerId?> TryGetPeerIdByIdentityPublicKeyHashAsync(byte[] recipientPublicKeyHash, CancellationToken cancellationToken = default)
{
    var typed = Percolator.Identity.IdentityPublicKeyHash.FromBytes(recipientPublicKeyHash);
    return await TryGetPeerIdByIdentityPublicKeyHashAsync(typed, cancellationToken).ConfigureAwait(false);
}
```

### G.4: Update UI snapshots to hold IdentityPublicKeyHash

**File:** `Desktop.Wpf\Features\Simulator\Models\SimulatedRelayModel.cs`

Update `InboundRelayMessageSnapshot` to use typed value:
```csharp
public sealed record InboundRelayMessageSnapshot(
    Guid AckId,
    Percolator.Identity.IdentityPublicKeyHash TargetIdentityPublicKeyHash, // Changed from byte[]
    byte[] OpaqueBytes,
    DateTimeOffset EnqueuedUtc,
    string? DebugType);
```

Update creation:
```csharp
.Select(x => new InboundRelayMessageSnapshot(
    AckId: x.AckId,
    TargetIdentityPublicKeyHash: x.TargetPkh, // No ToArray()
    OpaqueBytes: x.OpaqueBytes.ToArray(),
    EnqueuedUtc: x.EnqueuedUtc,
    DebugType: x.DebugType))
```

**File:** `Desktop.Wpf\Features\Simulator\SimulatedRelayQueuePanelViewModel.cs`

Update view model to hold typed value:
```csharp
public sealed record SimulatedRelayQueueItemViewModel(
    Percolator.Network.PeerId RelayHostPeerId,
    Guid AckId,
    DateTimeOffset EnqueuedUtc,
    string? DebugType,
    Percolator.Identity.IdentityPublicKeyHash? TargetIdentityPublicKeyHash, // Changed from byte[]
    byte[] OpaqueBytes,
    IReadOnlyDictionary<Guid, string> PeerNameById);

// Update creation
targetIdentityPublicKeyHash: m is InboundRelayMessage inbound ? inbound.TargetPkh : null, // No ToArray()
```

### G.5: Update test mock setups to use typed matching

**Pattern:** Replace `h.ToArray().SequenceEqual(expectedPkh)` with `h.Equals(expectedPkh)`

Files to update:
- `Desktop.Wpf.Tests\SimulatorRelayDeliveryServiceTests.cs`
- `Percolator.ApplicationTests\Network\NetworkTransportPortAdapterTests.cs`
- `Percolator.ApplicationTests\Cli\RequestPreKeyBundleByPkhHandlerTests.cs`
- `Percolator.ApplicationTests\Apps\Chat\CreateGroupConversationHandlerTests.cs`

Example:
```csharp
// Before
.Setup(s => s.GetPeerIdByPublicKeyHashAsync(
    It.Is<IdentityPublicKeyHash>(h => h.ToArray().SequenceEqual(expectedPkh)),
    It.IsAny<CancellationToken>()))

// After
.Setup(s => s.GetPeerIdByPublicKeyHashAsync(
    It.Is<IdentityPublicKeyHash>(h => h.Equals(expectedPkh)),
    It.IsAny<CancellationToken>()))
```

---

## Chunk H

### Goal

Refactor all `ByteArrayRecord` implementations to follow the pattern established by `IdentityPublicKeyHash`: use `ReadOnlyMemory<byte>` internally, provide defensive copying only at boundaries, and eliminate unnecessary array allocations.

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
3. Multiple duplicate implementations across domains
4. No standard `ToArray()` / `AsReadOnlyMemory()` pattern
5. Hash code computation iterates array every time (not cached)

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

### Impact Analysis

**High-impact changes:**
- All 29 concrete ByteArrayRecord types need constructor updates
- ~20+ call sites in Cryptography domain use .Value.Length for validation
- ~10+ call sites use .Value for crypto API boundaries
- ~15+ test assertions use .Value for comparison

**Medium-impact changes:**
- Protobuf serialization boundaries need .ToArray() calls
- Dht domain has unique pattern with length validation (NodeId)
- Network domain types used in cross-domain boundaries

**Low-impact changes:**
- Chat domain has only 3 types
- Identity domain only has IdentityPublicKeyHash (already refactored)

### Design Pattern from IdentityPublicKeyHash

**Key improvements:**
1. Internal storage: `ReadOnlyMemory<byte>` instead of `byte[]`
2. Defensive copy in `FromBytes()` factory method
3. `ToArray()` returns defensive copy
4. `AsReadOnlyMemory()` exposes read-only view
5. Cached hash code (optional optimization)
6. Custom `Equals()` using `SequenceEqual`

### H.1: Consolidate ByteArrayRecord into a shared library

**Architectural decision:** Create a new `Percolator.Common` project to hold shared value object infrastructure. This avoids code duplication while maintaining domain isolation (no domain references another domain).

**File:** `source/Percolator.Common/Primitives/ByteArrayRecord.cs` (new file)
```csharp
namespace Percolator.Common.Primitives;

public abstract record ByteArrayRecord
{
    private readonly ReadOnlyMemory<byte> _value;
    private readonly int _cachedHashCode;

    protected ByteArrayRecord(ReadOnlyMemory<byte> value)
    {
        _value = value;
        _cachedHashCode = ComputeHashCode(value.Span);
    }

    /// <summary>
    /// Creates a ByteArrayRecord from raw bytes with a defensive copy.
    /// </summary>
    protected static byte[] CopyBytes(byte[] bytes)
    {
        if (bytes is null)
            throw new ArgumentNullException(nameof(bytes));
        
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        return copy;
    }

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

    public override int GetHashCode() => _cachedHashCode;

    private static int ComputeHashCode(ReadOnlySpan<byte> span)
    {
        unchecked
        {
            var hash = 17;
            for (int i = 0; i < span.Length; i++)
            {
                hash = hash * 23 + span[i];
            }
            return hash;
        }
    }
}
```

### H.2: Update all ByteArrayRecord implementations to inherit from shared base

**H.2.1: Percolator.Identity.Primitives.ByteArrayRecord**

**File:** `source/Percolator.Identity/Primitives/ByteArrayRecord.cs`
```csharp
using Percolator.Common.Primitives;

namespace Percolator.Identity.Primitives;

public abstract record ByteArrayRecord : Percolator.Common.Primitives.ByteArrayRecord
{
    protected ByteArrayRecord(byte[] bytes) 
        : base(CopyBytes(bytes))
    {
    }
}
```

**H.2.2: Percolator.Chat.Primitives.ByteArrayRecord**

**File:** `source/Percolator.Chat/Primitives/ByteArrayRecord.cs`
```csharp
using Percolator.Common.Primitives;

namespace Percolator.Chat.Primitives;

public abstract record ByteArrayRecord : Percolator.Common.Primitives.ByteArrayRecord
{
    protected ByteArrayRecord(byte[] bytes) 
        : base(CopyBytes(bytes))
    {
    }
}
```

**H.2.3: Percolator.Network.Primitives.ByteArrayRecord**

**File:** `source/Percolator.Network/Primitives/ByteArrayRecord.cs`
```csharp
using Percolator.Common.Primitives;

namespace Percolator.Network.Primitives;

public abstract record ByteArrayRecord : Percolator.Common.Primitives.ByteArrayRecord
{
    protected ByteArrayRecord(byte[] bytes) 
        : base(CopyBytes(bytes))
    {
    }
}
```

**H.2.4: Percolator.Cryptography.Primitives.ByteArrayRecord`

**File:** `source/Percolator.Cryptography/Primitives/ByteArrayRecord.cs`
```csharp
using Percolator.Common.Primitives;

namespace Percolator.Cryptography.Primitives;

public abstract record ByteArrayRecord : Percolator.Common.Primitives.ByteArrayRecord
{
    protected ByteArrayRecord(byte[] bytes) 
        : base(CopyBytes(bytes))
    {
    }
}
```

**H.2.5: Percolator.Dht.Primitives.ByteArrayRecord**

**File:** `source/Percolator.Dht/Primitives/ByteArrayRecord.cs`
```csharp
using Percolator.Common.Primitives;

namespace Percolator.Dht.Primitives;

public abstract record ByteArrayRecord : Percolator.Common.Primitives.ByteArrayRecord
{
    protected ByteArrayRecord(byte[] bytes) 
        : base(CopyBytes(bytes))
    {
    }

    // Dht has existing Value property - override to delegate
    public byte[] Value => ToArray();
}
```

### H.3: Update ByteArrayRecord implementations to use proper construction

**H.3.1: Percolator.Chat.ValueObjects.Pkh**

**File:** `source/Percolator.Chat/ValueObjects/Pkh.cs`
```csharp
using Percolator.Chat.Primitives;

namespace Percolator.Chat.ValueObjects;

public sealed record Pkh : ByteArrayRecord
{
    public Pkh(byte[] value) : base(value) { }

    public static Pkh FromBytes(byte[] value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return new Pkh(value);
    }
}
```

**H.3.2: Percolator.Network.ValueObjects.IdentityPublicKey**

**File:** `source/Percolator.Network/ValueObjects/IdentityPublicKey.cs`
```csharp
using Percolator.Network.Primitives;

namespace Percolator.Network.ValueObjects;

public record IdentityPublicKey : ByteArrayRecord
{
    public IdentityPublicKey(byte[] value) : base(value) { }
}
```

**H.3.3: Percolator.Cryptography types**

All cryptography types inherit from ByteArrayRecord. Update constructors to use base class:
- `AssociatedData.cs`
- `ChainKey.cs`
- `Ciphertext.cs`
- `HandshakeResponseMessage.cs`
- `HandshakeInvitation.cs`
- `OneTimeKey.cs`
- `Plaintext.cs`
- `PreKey.cs`
- `PrivateEphemeralKey.cs`
- `PrivateOneTimeKey.cs`
- `RatchetEphemeralKey.cs`
- `RatchetIdentityKey.cs`
- `PublicKey.cs`
- `PrivatePreKey.cs`
- `RootKey.cs`
- `Signature.cs`
- `SharedSecret.cs`
- `SessionRatchetMessage.cs`

**H.3.4: Percolator.Network types**

- `Payload.cs`
- `TlsCertificate.cs`
- `Signature.cs`
- `DirectMessagePublicKey.cs`
- `PublicKeyHash.cs`
- `PublicKey.cs`

**H.3.5: Percolator.Chat types**

- `GroupAvatar.cs`
- `EncryptedGroupKey.cs`

**H.3.6: Percolator.Dht types**

- `NodeId.cs` (already has length validation, preserve it)

### H.4: Update call sites to use ToArray() only at boundaries

**H.4.1: Protobuf serialization boundaries**

Search for `.Value` usage in protobuf conversion code and replace with `.ToArray()`:
```csharp
// Before
ByteString.CopyFrom(record.Value)

// After
ByteString.CopyFrom(record.ToArray())
```

**H.4.2: Persistence/JSON boundaries**

Search for `.Value` usage in serialization code and replace with `.ToArray()`:
```csharp
// Before
JsonSerializer.Serialize(record.Value)

// After
JsonSerializer.Serialize(record.ToArray())
```

**H.4.3: Cryptography API boundaries**

Some cryptography APIs accept `byte[]` directly. Update call sites:
```csharp
// Before
cryptoApi.Process(record.Value)

// After
cryptoApi.Process(record.ToArray())
```

**H.4.4: Test assertions**

Update test assertions to use proper equality:
```csharp
// Before
Assert.That(actual.Value.SequenceEqual(expected.Value))

// After
Assert.That(actual.Equals(expected))
```

### H.5: Remove duplicate ByteArrayRecord implementations

After consolidating to shared base, remove the duplicate implementations in:
- `Percolator.Identity.Primitives.ByteArrayRecord.cs`
- `Percolator.Chat.Primitives.ByteArrayRecord.cs`
- `Percolator.Network.Primitives.ByteArrayRecord.cs`
- `Percolator.Cryptography.Primitives.ByteArrayRecord.cs`

Keep only the thin wrapper that inherits from `Percolator.Common.Primitives.ByteArrayRecord` to maintain domain isolation.

### Notes

- The shared base class approach maintains domain isolation (no cross-domain references)
- Defensive copying happens at construction boundaries only
- Internal operations use `ReadOnlyMemory<byte>` without allocation
- `ToArray()` is called only at external boundaries (protobuf, persistence, crypto APIs)
- Hash code is cached for performance
- This pattern is already proven by `IdentityPublicKeyHash`

---