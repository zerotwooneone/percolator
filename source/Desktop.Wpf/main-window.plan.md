# Bi-Directional Chat Plan (Main ↔ Simulator)

**Goal:** Secure bi-directional chat messaging between the Main Window and Simulated Peers over established Direct and Relayed sessions.

## Architecture Guidelines Checklist

| Guideline | Source | Rule |
|---|---|---|
| Thread-agnostic services | `r3.readme.md` | State services must not inject `IUiDispatcher`. Mutations under `SemaphoreSlim`/`lock`. |
| ViewModel UI bridging | `wpf.readme.md` | ViewModels own the dispatcher. Collections via `CreateView` → `ToNotifyCollectionChanged(ui.CollectionEventDispatcher)`. Properties via `ObserveOnCurrentSynchronizationContext()`. |
| Domain snapshotting | `r3.readme.md` | `.Freeze()` under lock produces pure immutable records for background I/O. |
| No ViewModel sorting | `wpf.readme.md` | Sorting in XAML via `CollectionViewSource.GetDefaultView(...)` + `CustomSort`. |
| Robust tests | `unit-testing.md` | AAA pattern, black-box, no internal-state assertions. Mock external deps only. |
| Domain isolation | Architecture | Domain projects never reference each other or Application. Cross-domain goes via Application. |
| Contracts project | Architecture | Only protobuf defs. No C# interfaces. |

## Pre-existing Concern: Potential Double-Send

`PostTextMessageHandler` (Application layer) currently sends `ChatEnvelope` directly to all participants (lines 69-76) AND publishes `TextMessagePostedEvent`. `TextMessagePostedHandler` catches that event and dispatches `DispatchTextMessageCommand`, which sends again via `IRemoteEnvelopeSender` (with PKH resolution for relay fallback).

Treat this as a **bug** (duplicate send risk). The preferred fix (when you address it) is:

- Keep the `TextMessagePostedEvent` → `DispatchTextMessageCommand` pipeline as the **only** outbound network send.
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

**Important type constraint:** In this codebase, `ParticipantId` is a `Guid` wrapper. Any PKH→peer lookup used for chat author attribution must return a `Guid`-backed identifier (not an `int`).

---

## Chunk A: Separate Inbound vs Outbound Chat Commands

**Problem:** `ProcessInternalEnvelopeHandler` currently dispatches inbound text messages via `PostTextMessageCommand`, whose handler sends the message over the network to all participants AND publishes `TextMessagePostedEvent`. The event then triggers `TextMessagePostedHandler` → `DispatchTextMessageCommand` → another network send. This causes an echo loop: received messages are re-broadcasted.
Additionally, `SqliteChatMessageWriter.AddTextMessageAsync(...)` currently determines `SenderId` by selecting the participant that is *not* `SelfIdentity.PeerId`. Therefore, the current persistence API cannot correctly represent who authored the message and will mis-attribute outbound messages.

**Solution:** Split inbound vs outbound into separate commands *and* make message persistence **direction-aware** by explicitly providing the acting participant.
- Inbound processing must never reach outbound dispatch.
- Outbound processing must persist messages as authored by self.
- Inbound processing must persist messages as authored by the remote peer.

### A0: Refactor `IChatMessageWriter` to be direction-aware
- **File:** `Percolator.Chat/App/IChatMessageWriter.cs`
- **Change:** Replace the `selfIdentityId`-only methods with actor-aware methods. Make the actor explicit.

```csharp
Task AddTextMessageAsync(
    ConversationId conversationId,
    int selfIdentityId,
    ParticipantId senderId,
    string content,
    MessageId messageId,
    DateTimeOffset sentAt,
    CancellationToken cancellationToken);

Task AddReadReceiptAsync(
    ConversationId conversationId,
    int selfIdentityId,
    ParticipantId readerId,
    MessageId messageId,
    DateTimeOffset sentAt,
    CancellationToken cancellationToken);

Task AddDeliveredReceiptAsync(
    ConversationId conversationId,
    int selfIdentityId,
    ParticipantId recipientId,
    MessageId messageId,
    DateTimeOffset deliveredAt,
    CancellationToken cancellationToken);

Task AddEmojiAnnotationAsync(
    ConversationId conversationId,
    int selfIdentityId,
    ParticipantId reactorId,
    MessageId messageId,
    string emoji,
    DateTimeOffset sentAt,
    CancellationToken cancellationToken);
```

### A0.1: Update infrastructure writer implementation
- **File:** `Percolator.Infrastructure/Chat/SqliteChatMessageWriter.cs`
- **Change:** Remove the "pick participant not self" actor inference. Use the explicitly passed-in `senderId` / `readerId` / `recipientId` / `reactorId` when creating DB rows.

### A1: Introduce inbound commands and handlers (text + receipts + emoji)
Create inbound commands that resolve the conversation, persist with the correct actor ID, publish inbound-only events, and **do not** send anything over the network.

**New Command & Handler Files (in `Percolator.Chat/App/Commands/` and `Percolator.Chat/App/Handlers/`):**
Create the command and handler for Text Messages, Read Receipts, Delivered Receipts, and Emoji Annotations.

*Use this exact implementation for the Text Message handler to establish the pattern, and mirror it for the others:*

```csharp
public sealed record ReceiveTextMessageCommand(
    ConversationLookupKey LookupKey,
    ParticipantId SenderId,
    MessageId MessageId,
    string Content,
    DateTimeOffset SentTimestampUtc) : IRequest;

public sealed class ReceiveTextMessageHandler : IRequestHandler<ReceiveTextMessageCommand>
{
    private readonly IConversationResolver _resolver;
    private readonly IChatMessageWriter _writer;
    private readonly IPublisher _publisher;

    public ReceiveTextMessageHandler(
        IConversationResolver resolver,
        IChatMessageWriter writer,
        IPublisher publisher)
    {
        _resolver = resolver;
        _writer = writer;
        _publisher = publisher;
    }

    public async Task Handle(ReceiveTextMessageCommand request, CancellationToken cancellationToken)
    {
        request.LookupKey.EnsureExactlyOne();
        var resolution = await _resolver.ResolveAsync(request.LookupKey, cancellationToken).ConfigureAwait(false);

        await _writer.AddTextMessageAsync(
            resolution.Conversation.Id,
            resolution.SelfIdentityId,
            request.SenderId,
            request.Content,
            request.MessageId,
            request.SentTimestampUtc,
            cancellationToken).ConfigureAwait(false);

        await _publisher.Publish(new TextMessageReceivedEvent(
            resolution.Conversation.Id.Value,
            request.MessageId.Value,
            resolution.SelfIdentityId,
            request.SenderId.Value,
            request.Content,
            request.SentTimestampUtc), cancellationToken).ConfigureAwait(false);
    }
}
```

### A2: Add inbound-only events for UI projection
Create event types that are explicitly inbound and therefore must **not** be handled by application dispatch handlers.
- **New Files (in `Percolator.Chat/Events/`):**
  - `TextMessageReceivedEvent.cs`
  - `ReadReceiptReceivedEvent.cs`
  - `DeliveredReceiptReceivedEvent.cs`
  - `EmojiAnnotationReceivedEvent.cs`
- Each event must carry: `ConversationId`, `MessageId`, `SelfIdentityId`, the specific Actor ID, and Timestamp.

### A3: Make outbound posting explicit about actor
Update the existing outbound `Post*` handlers to explicitly pass the self actor ID into the refactored writer API.
- **Files:** `Percolator.Application/Apps/Chat/Handlers/PostTextMessageHandler.cs` (and equivalent receipt/emoji handlers).
- **Change:** After resolving the conversation, determine `selfParticipantId` via the existing `ActiveIdentityContext` implementation of `ISelfParticipantIdProvider`. Call the new `Add*Async` writer methods passing `selfParticipantId` as the actor.

### A4: Route inbound envelopes to inbound commands
- **File:** `Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`
- **Change:** Route `ChatEnvelope` cases (`TextMessage`, `ReadReceipt`, etc.) to their respective `Receive*Command` types.
- **Actor Resolution Logic:**
  - Group chat: Use `ChatEnvelope.TextMessage.AuthorIdentityKey` (SPKI) to compute PKH, then resolve to `ParticipantId`. Throw if missing.
  - Direct chat: Use `request.Context.RemotePeerGuid` to compute `ParticipantId`. Throw if missing.
- **Add the Resolver Adapter:**
  - Update `Percolator.Chat/App/IPkhPeerResolver.cs` signature: `Task<ParticipantId?> GetParticipantIdByPkhAsync(Pkh pkh, CancellationToken cancellationToken);`
  - Create `Percolator.Application/Apps/Chat/PkhPeerResolver.cs` implementing `IPkhPeerResolver`. Have it depend on `IPeerPublicSigningKeyStore`.
  - Register it in `Percolator.Application/Apps/Chat/ServiceCollectionExtensions.cs` as scoped.

### A5: Ensure application dispatch boundaries
Application network dispatch handlers must continue to listen **only** to `*PostedEvent` (outbound) and must not subscribe to any `*ReceivedEvent` types.

### A: Tests
- **Test 1:** `ReceiveTextMessageHandler` calls writer with `senderId = remoteParticipantId` and publishes `TextMessageReceivedEvent`.
- **Test 2:** `PostTextMessageHandler` calls writer with `senderId = selfParticipantId`.
- **Test 3:** `ProcessInternalEnvelopeHandler` routes inbound `ChatEnvelope.TextMessage` to `ReceiveTextMessageCommand`.
- **Test 4:** Assert no handler exists in `Percolator.Application` implementing `INotificationHandler<*ReceivedEvent>`.

### A: Definition of Done
- `IChatMessageWriter` is actor-aware.
- Inbound envelopes route to `Receive*` commands which persist and publish `*ReceivedEvent` only.
- Application dispatch handlers listen only to `*PostedEvent` (no echo loop).
- Unit tests pass.

## Chunk B: Register Production Chat Infrastructure in Desktop.Wpf

**Problem:** `Desktop.Wpf/App.xaml.cs` currently registers a mock `InMemoryChatHistory` as the chat persistence mechanism. The real SQLite-backed implementations from `Percolator.Infrastructure.Chat` (which correctly implement `IConversationRepository`, `IChatMessageWriter`, etc.) are not wired up to the Dependency Injection container.

**Solution:** Remove the mock registration and call the infrastructure registration extension method.

### B1: Add `AddChatInfrastructure()` call
- **File:** `Desktop.Wpf/App.xaml.cs`
- **Add Import:** At the top of the file, add `using Percolator.Infrastructure.Chat;`
- **Location:** Find the `ConfigureServices` method (or wherever services are registered). Look for this existing line:
  `services.AddInfrastructureServices(context.Configuration);` (This registers the `PercolatorDbContext`).
- **Action:** Immediately *after* `services.AddInfrastructureServices(...)` and *before* `services.AddApplicationServices(...)`, add the following line:
  ```csharp
  services.AddChatInfrastructure();
  ```
- **Constraint Note:** Everything registered by `AddChatInfrastructure()` is **scoped** because it depends on the Entity Framework `PercolatorDbContext`. This means these services can never be injected directly into a Singleton. Any Singleton (like the `ChatReloadCoordinator` we will build next) must use `IServiceScopeFactory` to resolve them.

### B2: Remove `InMemoryChatHistory` registration
- **File:** `Desktop.Wpf/App.xaml.cs`
- **Location:** Find the `IChatHistory` registration (likely around line 141, depending on recent changes).
- **Action:** Delete the following line entirely:
  ```csharp
  services.AddSingleton<IChatHistory, InMemoryChatHistory>();
  ```
*(Note: Do not delete the `IChatHistory.cs` or `InMemoryChatHistory.cs` files yet. We will delete them in Chunk D once `ChatViewModel` no longer depends on them to avoid breaking the build mid-refactor).*

### B: Tests
- **Manual Verification Only:**
  1. Build the `Desktop.Wpf` project.
  2. Launch the application.
  3. Confirm the application starts without any Dependency Injection or Entity Framework migration exceptions.

### B: Definition of Done
- `using Percolator.Infrastructure.Chat;` is present in `App.xaml.cs`.
- `services.AddChatInfrastructure();` is called in the DI composition root.
- `InMemoryChatHistory` registration is removed.
- Project compiles and launches successfully.

## Chunk C: Reactive Chat State Service & Model Refactor

**Problem:** The WPF layer needs a thread-agnostic, singleton state orchestrator that aggregates chat messages per session. To support high-performance "Upsert" diffing (preserving UI scroll position), we must cleanly separate the data snapshot from the reactive Domain Model and avoid memory leaks.

### C1: Refactor ChatMessage into Snapshot and Model
- **File:** `Desktop.Wpf/Features/Chat/ChatMessageModel.cs` (Replace/Rename existing `ChatMessage.cs`)
- **Action:** Create a pure snapshot record and a fully reactive `IDisposable` domain model. Ensure `DisposableBag` is used to prevent memory leaks on the reactive properties.

```csharp
using R3;
using System;

namespace Desktop.Wpf.Features.Chat;

public sealed record ChatMessageSnapshot(
    string Id, 
    string Author, 
    string Text, 
    DateTimeOffset Timestamp, 
    bool IsOwn, 
    bool IsDelivered, 
    bool IsRead);

public sealed class ChatMessageModel : IDisposable
{
    private DisposableBag _bag;

    public string Id { get; }
    public string Author { get; }
    public string Text { get; }
    public DateTimeOffset Timestamp { get; }
    public bool IsOwn { get; }

    public BindableReactiveProperty<bool> IsDelivered { get; }
    public BindableReactiveProperty<bool> IsRead { get; }

    public ChatMessageModel(ChatMessageSnapshot snapshot)
    {
        Id = snapshot.Id;
        Author = snapshot.Author;
        Text = snapshot.Text;
        Timestamp = snapshot.Timestamp;
        IsOwn = snapshot.IsOwn;

        IsDelivered = new BindableReactiveProperty<bool>(snapshot.IsDelivered).AddTo(ref _bag);
        IsRead = new BindableReactiveProperty<bool>(snapshot.IsRead).AddTo(ref _bag);
    }

    public void UpdateFromSnapshot(ChatMessageSnapshot snapshot)
    {
        IsDelivered.Value = snapshot.IsDelivered;
        IsRead.Value = snapshot.IsRead;
    }

    public void Dispose() => _bag.Dispose();
}
```

### C2: Create `ChatStateService` (Pure Sync Pattern)
- **File:** `Desktop.Wpf/Features/Chat/State/ChatStateService.cs`
- **Behavior:** Owns in-memory `ObservableList<ChatMessageModel>`, uses a state gate lock, and publishes `StateMutated`. Use the **Sync** pattern instead of Replace to preserve UI scroll state.

```csharp
using ObservableCollections;
using R3;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Desktop.Wpf.Features.Chat.State;

public sealed class ChatStateService : IDisposable
{
    private readonly ConcurrentDictionary<string, ObservableList<ChatMessageModel>> _sessionMessages = new();
    private readonly Subject<Unit> _stateMutated = new();
    private readonly object _stateGate = new();

    public Observable<Unit> StateMutated => _stateMutated;

    public ObservableList<ChatMessageModel> GetOrAddSessionMessagesList(string sessionId) 
        => _sessionMessages.GetOrAdd(sessionId, _ => new ObservableList<ChatMessageModel>());

    public void SyncMessages(string sessionId, IReadOnlyList<ChatMessageSnapshot> snapshots)
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
        
        _stateMutated.OnNext(Unit.Default);
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            foreach (var list in _sessionMessages.Values)
            {
                foreach (var msg in list) msg.Dispose();
                list.Clear();
            }
            _sessionMessages.Clear();
        }
        _stateMutated.Dispose();
    }
}
```

### C3: Create `ChatReloadCoordinator` (Debounced async load)
- **File:** `Desktop.Wpf/Features/Chat/ChatReloadCoordinator.cs`
- **Behavior:**
  - Depends on `ChatStateService` and `IServiceScopeFactory`.
  - Exposes `public void TriggerReloadForSession(string sessionId)`.
  - Must use `SelectAwait` to safely handle asynchronous database calls within the R3 pipeline.

**Implement the pipeline exactly like this:**
```csharp
private readonly Subject<string> _reloadTrigger = new();
private readonly DisposableBag _bag = new();

// In the constructor:
_reloadTrigger
    .Debounce(TimeSpan.FromMilliseconds(50))
    .SelectAwait(async (sessionId, ct) =>
    {
        await ReloadCoreAsync(sessionId, ct).ConfigureAwait(false);
        return Unit.Default;
    }, AwaitOperation.Drop) // Drop prevents overlapping DB queries for the same session burst
    .Subscribe()
    .AddTo(ref _bag);

public void TriggerReloadForSession(string sessionId)
{
    _reloadTrigger.OnNext(sessionId);
}
```

**In the `ReloadCoreAsync` method:**
- Create a scope: `using var scope = _scopeFactory.CreateScope();`
- Resolve `IConversationRepository`.
- Load messages via `IConversationRepository.GetByIdAsync(...)`.
- Map the DB `Percolator.Chat.Message` entities into `Desktop.Wpf.Features.Chat.ChatMessageSnapshot` records. (Use a simple property-to-property mapping. For `IsOwn`, check if the message `SenderId` matches the current `ActiveIdentityContext`'s Self Participant ID).
- Call `_state.SyncMessages(sessionId, mappedSnapshots)`.

### C4: Create `ChatStateUpdateHandlers` (Live MediatR Updates)
- **File:** `Desktop.Wpf/Features/Chat/Handlers/ChatStateUpdateHandlers.cs`
- **Behavior:** Implements `INotificationHandler<TextMessagePostedEvent>` and `INotificationHandler<TextMessageReceivedEvent>`.
- **Action:** - If the `ConversationId` maps to a known open session, map the event data into a `ChatMessageSnapshot`.
  - Pass the mapped snapshot to `_state.SyncMessages(sessionId, new[] { snapshot })`.
  - *(Note: A robust dev will also trigger `ChatReloadCoordinator.TriggerReloadForSession` as a fallback, but pushing the single message snapshot is faster for the UI).*

### C: Definition of Done
- `ChatMessage` is successfully split into `ChatMessageSnapshot` and `ChatMessageModel`.
- `ChatMessageModel` safely implements `IDisposable` with a `DisposableBag`.
- `ChatStateService` manages messages via the `SyncMessages` (Upsert) pattern.
- The reload coordinator and update handlers successfully map infrastructure/domain events into Snapshots and feed them into the State Service.

## Chunk D: Pure Projection ChatViewModel

**Problem:** `ChatViewModel` currently uses a mock `IChatHistory` abstraction and a manual `ObservableCollection`. It must be refactored to project its state purely from the `ChatStateService` using R3 reactive patterns, and it must use WPF native sorting to arrange the messages chronologically without mutating the domain.

### D1: Implement the WPF Chronological Comparer
- **File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
- **Action:** At the bottom of the file (outside the `ChatViewModel` class but inside the namespace), add a custom comparer to sort the ViewModels chronologically. Chat history should be sorted ascending (oldest at the top, newest at the bottom).

```csharp
public sealed class ChatMessageChronologicalComparer : System.Collections.IComparer
{
    public int Compare(object? x, object? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return 1;
        if (y is null) return -1;
        
        var msgX = (ChatMessageModel)x;
        var msgY = (ChatMessageModel)y;
        
        // Ascending: oldest at the top, newest at the bottom
        return msgX.Timestamp.CompareTo(msgY.Timestamp);
    }
}
```

### D2: Rewrite `ChatViewModel` Dependencies and Fields
- **File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
- **Action:** 1. Remove the `IChatHistory _history` field entirely.
  2. Add the following required constructor dependencies:
    - `Desktop.Wpf.Features.Chat.State.ChatStateService _chatState`
    - `ChatReloadCoordinator _reloadCoordinator`
    - `IMediator _mediator`
    - `IUiDispatcher _ui`
  3. Add the reactive tracking fields:
     ```csharp
     private ISynchronizedView<ChatMessageModel, ChatMessageModel>? _messagesView;
     private INotifyCollectionChangedSynchronizedViewList<ChatMessageModel>? _messagesSyncList;
     ```
  4. Change the `Messages` property type to `object?` so WPF can bind to the `ListCollectionView`:
     ```csharp
     public object? Messages { get; private set; }
     ```

### D3: Implement `SetSession` (The Reactive Pipeline)
- **File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
- **Action:** Replace the existing `SetSession` logic with the following reactive projection pipeline. Note that `SetSession` is called exactly once per scoped instance.

```csharp
public void SetSession(string sessionId)
{
    _sessionId = sessionId;

    // Trigger initial background load from SQLite
    _reloadCoordinator.TriggerReloadForSession(sessionId);

    // 1. Get the raw, unsorted domain list
    var domainList = _chatState.GetOrAddSessionMessagesList(sessionId);

    // 2. Create a pass-through view and track it
    _messagesView = domainList.CreateView(m => m).AddTo(ref _bag);

    // 3. Bridge the view to the WPF UI thread
    _messagesSyncList = _messagesView.ToNotifyCollectionChanged(_ui.CollectionEventDispatcher);

    // 4. Wrap it in a WPF CollectionView for native chronological sorting
    var collectionView = (System.Windows.Data.ListCollectionView)System.Windows.Data.CollectionViewSource.GetDefaultView(_messagesSyncList);
    collectionView.CustomSort = new ChatMessageChronologicalComparer();
    
    Messages = collectionView;

    // 5. Force the UI to refresh its sort when background mutations occur
    _chatState.StateMutated
        .ObserveOnCurrentSynchronizationContext()
        .Subscribe(_ => collectionView.Refresh())
        .AddTo(ref _bag);
}
```

### D4: Rewrite `SendCommand` to use MediatR
- **File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
- **Action:** Update the `SendCommand` execution logic to dispatch the new outbound command you built in Chunk A.

```csharp
SendCommand = new AsyncRelayCommand(async _ =>
{
    if (_sessionId is null) return;
    var text = MessageInput.Value;
    if (string.IsNullOrWhiteSpace(text)) return;

    var lookup = ConversationLookupKey.ForDirectSession(Guid.Parse(_sessionId));
    var messageId = new MessageId(Guid.NewGuid());
    var sentTs = DateTimeOffset.UtcNow;

    await _mediator.Send(new PostTextMessageCommand(lookup, messageId, text, sentTs));

    MessageInput.Value = string.Empty;
}, _ => CanSend.Value);
```

### D5: Update `DisposeCore`
- **File:** `Desktop.Wpf/Features/Chat/ChatViewModel.cs`
- **Action:** Ensure the UI resources are released. Add `_messagesSyncList?.Dispose();` inside `DisposeCore()`.

### D6: Clean Up Dead Code
- **Action:** Delete `Desktop.Wpf/Features/Chat/IChatHistory.cs` and `Desktop.Wpf/Features/Chat/InMemoryChatHistory.cs`. Ensure no references to these files remain.

### D: Definition of Done
- `ChatViewModel` has no dependency on `IChatHistory`.
- `Messages` is exposed as an `object?` containing a sorted WPF `ListCollectionView`.
- Sending a message routes purely through `IMediator` (`PostTextMessageCommand`).
- Dead files are deleted.
- The project compiles and the chat UI loads properly.

## Chunk E: Simulator Rolling Chat State

**Problem:** Simulated peers need to remember a bounded window of recent chat messages for debugging and display. This must use the existing Domain Snapshot pattern (`Freeze()` / `PeerStateSnapshot`) and persist via JSON without breaking the current hydration flows.

### E1: Add chat snapshot record to `PeerStateSnapshot`
- **File:** `Desktop.Wpf/Features/Simulator/PeerStateSnapshot.cs`
- **Action 1:** Add a new record near the other snapshot records in the file:
  ```csharp
  public sealed record SimulatedChatMessageSnapshot(bool IsFromMain, string Content, DateTimeOffset ReceivedUtc);
  ```
- **Action 2:** Update the `PeerStateSnapshot` primary constructor to include the new list. Append it at the very end of the parameter list to minimize churn:
  ```csharp
  public sealed record PeerStateSnapshot(
      // ... existing parameters ...
      IReadOnlyList<SimulatedChatMessageSnapshot> RecentChatMessages);
  ```

### E2: Add bounded recent chat state to `SimulatedPeerModel`
- **File:** `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`
- **Action 1:** Add the tracking lists as fields/properties:
  ```csharp
  private readonly ObservableList<SimulatedChatMessageSnapshot> _recentChatMessages = new();
  public IReadOnlyObservableList<SimulatedChatMessageSnapshot> RecentChatMessages => _recentChatMessages;
  internal ObservableList<SimulatedChatMessageSnapshot> RecentChatMessagesMutable => _recentChatMessages;
  ```
- **Action 2:** Add the mutation method (bounded to 50 messages):
  ```csharp
  public void AddChatMessage(bool isFromMain, string content, DateTimeOffset receivedUtc)
  {
      const int MaxMessages = 50;
      _recentChatMessages.Add(new SimulatedChatMessageSnapshot(isFromMain, content, receivedUtc));
      while (_recentChatMessages.Count > MaxMessages)
      {
          _recentChatMessages.RemoveAt(0);
      }
  }
  ```

### E3: Extend `Freeze()` to include recent chat
- **File:** `Desktop.Wpf/Features/Simulator/SimulatedPeerModel.cs`
- **Action:** Inside the `Freeze()` method, update the `new PeerStateSnapshot(...)` instantiation to include the new field:
  `RecentChatMessages: _recentChatMessages.ToList()`

### E4: Hydrate recent chat in `SimulatorStateService`
- **File:** `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- **Action:** Update `CreatePeerFromSnapshot(PeerStateSnapshot snap)`. After constructing `var model = new SimulatedPeerModel(...);` but *before* returning it, hydrate the mutable list:
  ```csharp
  foreach (var m in snap.RecentChatMessages)
  {
      model.RecentChatMessagesMutable.Add(m);
  }
  ```

### E5: Persist recent chat via JSON DTO mapping
- **File:** `Desktop.Wpf/Features/Simulator/JsonSimulatorStateRepository.cs`
- **Action 1:** Add DTO representations at the bottom of the file (or wherever `SimulatedPeerDto` is defined):
  ```csharp
  public sealed class SimulatedChatMessageDto
  {
      public bool IsFromMain { get; set; }
      public string Content { get; set; } = "";
      public DateTimeOffset ReceivedUtc { get; set; }
  }
  ```
- **Action 2:** Add the list property to `SimulatedPeerDto`:
  ```csharp
  public List<SimulatedChatMessageDto> RecentChatMessages { get; set; } = new();
  ```
- **Action 3:** Update `CreateDto(PeerStateSnapshot model)`:
  ```csharp
  RecentChatMessages = model.RecentChatMessages.Select(m => new SimulatedChatMessageDto
  {
      IsFromMain = m.IsFromMain,
      Content = m.Content,
      ReceivedUtc = m.ReceivedUtc
  }).ToList()
  ```
- **Action 4:** Update `CreatePeerSnapshot(SimulatedPeerDto dto)`: After creating `SimulatedPeerModel model = new SimulatedPeerModel(...)` and before calling `model.Freeze()`, hydrate the model:
  ```csharp
  foreach (var m in dto.RecentChatMessages)
  {
      model.RecentChatMessagesMutable.Add(new SimulatedChatMessageSnapshot(m.IsFromMain, m.Content, m.ReceivedUtc));
  }
  ```

### E6: Mark runtime tracker dirty on chat changes
- **File:** `Desktop.Wpf/Features/Simulator/Tracking/SimulatedPeerRuntimeTracker.cs`
- **Action:** Add the following subscription inside the constructor to ensure the JSON auto-saves when a chat message is received:
  ```csharp
  peer.RecentChatMessagesMutable.ObserveChanged().Subscribe(_ => _dirty.OnNext(Unit.Default)).AddTo(ref _bag);
  ```

### E: Definition of Done
- `SimulatedPeerModel` stores up to 50 recent chat messages.
- `Freeze()` captures them into `PeerStateSnapshot`.
- JSON round-trip preserves chat messages.
- `SimulatedPeerRuntimeTracker` triggers a save on chat changes.
- Project compiles successfully.

## Chunk F: Simulator Chat Ingress & Egress

**Problem:** Simulated peers must: (1) parse incoming chat envelopes from Main and store them, and (2) generate outbound chat envelopes back to Main over the correct cryptographic session.

### F1: Ingress — Direct path (`ReceiveOpaqueMessageFromMainAsync`)
- **File:** `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- **Action:** In `ReceiveOpaqueMessageFromMainAsync`, after the call that parses `InternalEnvelope` (look for `InternalEnvelope.Parser.ParseFrom(...)`), and immediately before the existing `PrekeyEnvelope` handling branch, add the following check:

```csharp
if (env.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope
    && env.ChatEnvelope?.MessageCase == ChatEnvelope.MessageOneofCase.TextMessage)
{
    var text = env.ChatEnvelope.TextMessage;
    await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        model.AddChatMessage(
            isFromMain: true,
            content: text.Content,
            receivedUtc: text.SentTimestampUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow);
    }
    finally
    {
        _stateGate.Release();
    }
    _saveTrigger.OnNext(Unit.Default);
    return new DeliverOpaqueMessageResponse { Version = 1 };
}
```

### F2: Ingress — Relayed path (`ReceiveRelayedOpaquePayloadAsync`)
- **File:** `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs`
- **Problem:** `ReceiveRelayedOpaquePayloadAsync` currently only tries to parse `opaqueBytes` as `EstablishSessionResponse`. It needs to attempt session decryption for chat messages.
- **Action:** After the existing `EstablishSessionResponse` parse attempt (the try/catch that returns when it succeeds), and immediately before the final `return null;`, add this decryption attempt block:

```csharp
// Try to decrypt as a ratchet message (chat or other encrypted payload from Main via relay)
try
{
    var cipher = new SessionRatchetMessage(opaqueBytes);
    var clock = ResolveClock();

    await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        var peerModel = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId);
        if (peerModel is null) return null;

        foreach (var kv in peerModel.SessionsMutable)
        {
            try
            {
                var pt = kv.Value.Decrypt(cipher, clock);
                peerModel.SessionsMutable[kv.Key] = kv.Value;

                if (pt.Value.Length == 0) return null;

                var innerEnv = InternalEnvelope.Parser.ParseFrom(pt.Value);
                if (innerEnv.ApplicationPayloadCase == InternalEnvelope.ApplicationPayloadOneofCase.ChatEnvelope
                    && innerEnv.ChatEnvelope?.MessageCase == ChatEnvelope.MessageOneofCase.TextMessage)
                {
                    var txt = innerEnv.ChatEnvelope.TextMessage;
                    peerModel.AddChatMessage(
                        isFromMain: true,
                        content: txt.Content,
                        receivedUtc: txt.SentTimestampUtc?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow);
                    _saveTrigger.OnNext(Unit.Default);
                }
                return null; // Decrypted successfully, handled
            }
            catch { /* not this session */ }
        }
    }
    finally
    {
        _stateGate.Release();
    }
}
catch { /* not a ratchet message */ }
```

### F3: Egress — `SendChatMessageToMainAsync`
- **File:** `Desktop.Wpf/Features/Simulator/SimulatorStateService.cs` (and `ISimulatorStateService.cs`)
- **Action 1:** Add the method signature to the interface:
  `Task SendChatMessageToMainAsync(Guid simulatedPeerId, string content, CancellationToken cancellationToken = default);`
- **Action 2:** Implement the public method in the service. Note the deterministic session selection query.

```csharp
public async Task SendChatMessageToMainAsync(Guid simulatedPeerId, string content, CancellationToken cancellationToken = default)
{
    cancellationToken.ThrowIfCancellationRequested();

    var chatEnvelope = new ChatEnvelope
    {
        TextMessage = new TextMessage
        {
            MessageId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Content = content,
            SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        }
    };
    var internalEnvelope = new InternalEnvelope { ChatEnvelope = chatEnvelope };

    await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try
    {
        var model = _peers.FirstOrDefault(p => p.PeerId == simulatedPeerId)
            ?? throw new InvalidOperationException($"No simulated peer with id {simulatedPeerId}");

        // Session selection: Select the most recently created session
        var sessionId = model.SessionsMutable
            .OrderByDescending(kv => kv.Value.CreatedAtUtc)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        if (sessionId == default)
            throw new InvalidOperationException($"No session found for peer {simulatedPeerId} to send chat message");

        // Encrypt using the existing helper
        var cipher = await EncryptInternalEnvelopeAsync(simulatedPeerId, sessionId, internalEnvelope, cancellationToken)
            .ConfigureAwait(false);

        // Record outbound message in peer's chat history
        model.AddChatMessage(isFromMain: false, content: content, receivedUtc: DateTimeOffset.UtcNow);

        // Route based on connection mode
        if (model.ConnectionMode.CurrentValue == ConnectionMode.Relay)
        {
            var relayHostPeerId = model.RelayPeerId.CurrentValue;
            if (relayHostPeerId != Guid.Empty)
            {
                await EnqueueRelayUpstreamToMainAsync(
                    relayHostPeerId: relayHostPeerId,
                    opaqueBytes: cipher.Value,
                    debugType: "Chat",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            using var scope = _scopeFactory.CreateScope();
            var messageService = scope.ServiceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>();

            var request = new DeliverOpaqueMessageRequest
            {
                Version = 1,
                Payload = ByteString.CopyFrom(cipher.Value)
            };

            var ctx = new ServerCallContextStub(
                method: "/percolator.contracts.TransportService/DeliverOpaqueMessage",
                peer: "ipv4:127.0.0.1:0",
                deadline: DateTime.UtcNow.AddMinutes(1),
                requestHeaders: new Metadata(),
                cancellationToken: cancellationToken);

            _ = await messageService.DeliverOpaqueMessage(request, ctx).ConfigureAwait(false);
        }
    }
    finally
    {
        _stateGate.Release();
    }
    _saveTrigger.OnNext(Unit.Default);
}
```

### F: Definition of Done
- Incoming chat messages from Main are decrypted and stored in simulated peer's `RecentChatMessages` (both direct and relayed paths).
- `SendChatMessageToMainAsync` encrypts a chat message and routes it to Main (direct or relay).
- Project compiles and all integration features work as expected.