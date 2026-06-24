
## Rules of Execution for AI Agents

1. **File Locations & Namespaces:**
   - Persistence (DBOs): `Percolator.Infrastructure/Chat/Persistence`
   - Commands/Handlers: `Percolator.Application/Apps/Chat`
   - Interfaces/Queries (Contracts): `Percolator.Application/Chat`
   - Query Implementations: `Percolator.Infrastructure/Chat`
2. **EF Migrations:** To generate migrations, use PowerShell and run: `dotnet ef migrations add <MigrationName> --project source\Percolator.Infrastructure --startup-project source\Percolator.Node`
3. **WPF UI Targets:**
   - Group Action Menu (above chat): Modify `Desktop.Wpf/Features/Chat/ChatView.xaml`.
   - "Create Group" Tab & Multi-select: Modify `Desktop.Wpf/Features/Sessions/ConnectionManagementDialogWindow.xaml` and `ConnectionManagementDialogViewModel.cs`.
   - Pending Invites Display: Also modify `ConnectionManagementDialogWindow.xaml` / `ConnectionManagementDialogViewModel.cs`.
   - Sidebar UI Updates: Modify `Desktop.Wpf/Features/Sessions/SessionsSidebarView.xaml`.
4. **Dialogs:** To open new dialogs (e.g., for picking a member to add), do NOT create raw `Window` instances directly in ViewModels. Instead, use `IWindowManager.ShowFor<YourNewViewModel>()` matching the pattern seen in `PendingHandshakesMenuViewModel.cs`. You will need to create the View/ViewModel pair and map them in `Desktop.Wpf/Shared/Windowing/ViewMappings.xaml`.
5. **ByteArray Factory Methods:** ByteArray types (GroupMasterKey, GroupId, BlobKey, Ciphertext) have no public constructor. Use factory methods:
   - `FromBytesOwned(byte[])` - unsafe no-copy, use only when the array is strictly owned by the called code and never accessed elsewhere
   - `FromBytes(byte[])` - safe copy, use when the array is not owned by the called code or could be mutated elsewhere (e.g., EF Core entities, protobuf messages)
   - `FromSpan(ReadOnlySpan<byte>)` - useful for copying one ByteArray type to another
6. **PeerId:**
   - PeerId is a GUID
   - PeerId is a local only identifier, it must NEVER be sent over the wire
7. **No Shims or Temporary Code:** Do not implement shims or temporary code that does not exist in the plan. Do not write methods that throw `new NotImplementedException` - instead stop and ask the user what should be done. Each chunk must implement zero guesses. 

---

## Group Messaging Implementation Plan (V2)

**Design** Group messaging will be implemented very similar to the Signal protocol. However, as this is a peer to peer application the user must choose a single relay to host the group conversation when the group is created. All group members will need to establish a 1:1 session with the relay before they can participate in the group conversation. The relay maintains the group state - but that state is opaque to the relay.

## Chunk 1 ✅ COMPLETE
The goal of this chunk was to establish the local user's primary device identity and generate a 32-byte symmetric profile key. This key was used to securely encrypt the user's Display Name and transmit it over the existing 1:1 Double Ratchet channel, laying the foundation for profile sharing.
---
## Chunk 2 ✅ COMPLETE
This chunk focused on building a clean, synchronous interop bridge that connects the managed cryptographic layer directly to the SQLite database. It enabled querying and persisting unmanaged Sender Key states using isolated Entity Framework transactions, ensuring cryptographic states commit immediately without relying on ambient transactions.
---

## Chunk 3 ✅ COMPLETE
The objective here was to establish a Micro-PKI for the "Sealed Sender" feature, allowing relay nodes to securely generate and store an Ed25519 Root Key. Clients can now authenticate over standard TLS connections using cryptographic header signatures to request short-lived Delivery Certificates.
---

## Chunk 3.1 ✅ COMPLETE
This chunk completed the Micro-PKI infrastructure that was deferred from Chunk 3. It implemented the native Ed25519 interop wrappers, enabled persisting the Relay Root Key, and introduced a background worker to proactively refresh the local delivery certificate.
---
## Chunk 4 ✅ COMPLETE
This chunk implemented the Signal Protocol Group V2 Relay Ledger and Fan-Out mechanism. It introduced atomic ledger updates and message queue inserts to ensure concurrency control via manual version checking, while also establishing the core external gRPC contracts for handling incoming group message publish requests.
---
## Chunk 5
### Feature Implementation Request: Signal Protocol Chunk 5 (Group Provisioning via Outbox)
You are to implement Group Provisioning using an Outbox pattern to ensure group creation and subsequent invitations are atomic and resilient to network failures.

The Goal: Alice creates a group, persists the state atomically, and creates an outbox message. A background worker dispatches the invite over 1:1 encrypted tunnels.

Architectural Constraints (CRITICAL):
* **Explicit Persistence:** Do not implement a complex EF Core DbContext Interceptor. The Application layer orchestrates saving by calling `await _groupRepository.SaveWithOutboxAsync(group, ct)`. The infrastructure implementation of this method handles saving the group state and mapping/serializing its raised domain events to the outbox table inside a single atomic `SaveChangesAsync` call.
* **Rule 6 Adherence:** `PeerId` is a local-only identifier and must never be written to wire-bound serialization structures or sent over the wire. The Outbox table must use wire-safe routing tokens (`DestinationPkhBytes`).
* **Rich Primitive Wrappers:** Strictly eliminate raw Guid and byte[] values from service signatures. All layers must use fully-qualified, isolated domain primitive types (`Percolator.Chat.ConversationId`, `Percolator.Chat.PeerId`, `Percolator.Chat.GroupMasterKey`, `Percolator.Chat.SenderKeyDistributionMessageBytes`).

Implementation Requirements
1. Domain Layer (`Percolator.Chat`)
* **GroupConversation Aggregate:**
    * Properties: `Percolator.Chat.ConversationId Id`, `Percolator.Chat.GroupMasterKey MasterKey`, `uint Epoch`, `List<GroupMember> Members`.
    * Methods: `void InviteMember(Percolator.Chat.PeerId peerId)` which updates state and registers a `MemberInvitedDomainEvent`. `void ClearDomainEvents()` to flush events post-persistence.
* **IDomainEvent (`Percolator.Chat/Events`):** Define a record for `MemberInvitedDomainEvent` containing `Percolator.Chat.ConversationId ConversationId`, `Percolator.Chat.PeerId PeerId`, and pre-generated `SenderKeyDistributionMessageBytes` distribution blob.

2. Infrastructure Layer (`Percolator.Infrastructure/Chat/Persistence`)
* **RelayOutboxDbo:** Create a DBO to store pending domain events: `Guid Id`, `string EventType`, `string PayloadJson`, `byte[] DestinationPkhBytes`, `DateTimeOffset? ProcessedAtUtc`. The `DestinationPkhBytes` column stores the pre-resolved Public Key Hash for the target `PeerId`, enabling the `OutboxDispatcherWorker` to dispatch without secondary lookups.
* **OutboxDispatcherWorker (`Percolator.Infrastructure/Chat`):** An `IHostedService` that runs periodically. It queries the Outbox table for unprocessed events, resolves the domain event, reads the pre-resolved `DestinationPkhBytes` directly from the row, calls the `IMessageService` to dispatch the invite, and marks the event as processed.
* **Transactional Atomicity Guarantee:** In alignment with our direct `SaveChangesAsync` repository pattern, `SaveWithOutboxAsync` must stage both the `GroupConversation` state transitions and the mapped `RelayOutboxDbo` rows against the same internal context instance before calling save, clearing domain events on the aggregate root immediately after a successful database commit. This allows Entity Framework Core to natively leverage SQLite's transaction engine to guarantee atomic persistence without requiring a custom Unit-of-Work block.
* **Transient Network Backoff:** The `OutboxDispatcherWorker` must encapsulate transient network exceptions (e.g., gRPC `RpcException` timeouts). If a peer is offline, the worker must catch the error, log a warning, back off sequentially using a non-blocking `Task.Delay`, and skip updating `ProcessedAtUtc` so the record is cleanly evaluated on the next loop.

3. Application Orchestration (`Percolator.Application/Apps/Chat`)
* **Anti-Corruption Layer Note:** `CreateGroupCommandHandler` (located in `Percolator.Application/Apps/Chat`) serves as the Anti-Corruption Layer. It accepts incoming application primitives, extracts their raw values, and uses code-generated factory methods (e.g., `Percolator.Chat.GroupMasterKey.FromBytesOwned()`) to cleanly initialize the core domain entities.
* **CreateGroupCommandHandler Execution:**
    * Generate `Percolator.Chat.GroupMasterKey` via `IGroupCryptographyService`.
    * Instantiate `GroupConversation` domain entity using `Percolator.Chat` local types.
    * Call `group.InviteMember(peerId)` for each initial member using `Percolator.Chat.PeerId`.
    * Save via `await _groupRepository.SaveWithOutboxAsync(group, ct)`.

4. Integration Anchor: Provisioning & Invite Ingress (`Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`)
* Update `ProcessInternalEnvelopeHandler` to include a dedicated switch case for `ChatEnvelope.MessageOneofCase.GroupInvite`:
  ```csharp
  case ChatEnvelope.MessageOneofCase.GroupInvite:
  {
      var invite = chat.GroupInvite;
      // Extract parameters, use IGroupMessageCryptographyService to import the distribution blob
      // Use IPendingGroupInvitationRepository to persist the inbound invitation metadata
      break;
  }
  ```

5. Network Contracts (`internal_messaging.proto`)
* Add `GroupInvite` message and integrate with `ChatEnvelope`:
  ```protobuf
  message GroupInvite {
    optional uint32 version = 1;
    optional bytes conversation_id = 2;
    optional string group_name = 3;
    optional bytes group_master_key = 4; // 32 bytes
    optional bytes sender_key_distribution_message = 5;
  }
  ```
* Add `group_invite = 16;` to the `ChatEnvelope` oneof configuration.

**Testing Requirements (Chunk 5):**
- `ProcessInternalEnvelopeHandler_Handle_ExtractsGroupInvitePayload_WhenEnvelopeMatchesSchema` - Test that ProcessInternalEnvelopeHandler correctly extracts and processes GroupInvite payload when the ChatEnvelope contains a GroupInvite message.
- `GroupV2SessionAndMessagingRoundTrip_DistributionMessageEnablesGroupMessaging` - Integration test where a generated GroupMasterKey creates a distribution message, a separate peer context processes that distribution message to bootstrap their session, and group messages encrypted by that peer can be successfully decrypted by the group creator.


---

## Chunk 6
### Feature Implementation Request: Signal Protocol Chunk 6 (The Streaming Data Plane)
You are to implement the high-velocity, real-time Data Plane for Group V2 messaging.

The Goal: Build the application's foundational server-streaming infrastructure to track concurrent active group peer connections and execute decoupled, non-blocking fan-out operations.

Architectural Constraints (CRITICAL):
* **Interface Segregation:** The Application layer must define `IGroupNotificationDispatcher`. The Infrastructure layer implements this interface using gRPC streams. The Application layer must never see or reference an `IServerStreamWriter` instance.
* **Multi-Stream Connection Matrix:** Because multiple distinct peers connect to a single group conversation, `GrpcGroupNotificationDispatcher` must map a single `ConversationId` to a collection of active streams. Implement a thread-safe look-up matrix utilizing a nested dictionary lookup, ensuring distinct connection instances are tracked safely without overwriting concurrent peer sessions.
* **Pragmatic Persistence Handling:** Do not wrap your SQLite database appends in artificial application-level semaphores or single-threaded loops. Trust the underlying SQLite engine's native locking mechanisms to serialize concurrent transactional writes seamlessly via standard non-blocking asynchronous calls.

Implementation Requirements
1. Interface Definition (`Percolator.Application/Chat`)
* Define: `public interface IGroupNotificationDispatcher { Task DispatchAsync(Percolator.Application.Chat.ConversationId conversationId, MessageDto message, CancellationToken ct); }`

2. Infrastructure Multi-Stream Tracking (`Percolator.Infrastructure/Chat`)
* Create `GrpcGroupNotificationDispatcher` implementing `IGroupNotificationDispatcher`.
* **Storage Matrix:** Maintain a thread-safe nested lookup using infrastructure-local types: `ConcurrentDictionary<Percolator.Application.Chat.ConversationId, ConcurrentDictionary<Percolator.Application.Chat.PeerId, IServerStreamWriter<GroupStreamResponse>>>`.
* **Methods:**
    * Implement `Task DispatchAsync(...)`: Safely extract the nested list of writers for the matching `Percolator.Application.Chat.ConversationId`, iterate through the connections, and invoke `.WriteAsync()` to fan out the payload across all active peer streams.
    * Expose helper registrations: `void RegisterStream(Percolator.Application.Chat.ConversationId conversationId, Percolator.Application.Chat.PeerId peerId, IServerStreamWriter<GroupStreamResponse> stream)` and `void UnregisterStream(Percolator.Application.Chat.ConversationId conversationId, Percolator.Application.Chat.PeerId peerId)`.
* Create `SqliteChatMessageWriter` inside `Percolator.Infrastructure/Chat` to handle straightforward, async-safe database appends directly via core entity framework operations.

3. Application Ingress Orchestration (`Percolator.Application/Apps/Chat`)
* Implement `GroupIngressService.ProcessGroupMessageAsync`:
    1. **Early-Gate Roster Validation:** Inside `GroupIngressService.ProcessGroupMessageAsync`, the service must execute a local database lookup or fast query projection against the conversation's active membership roster prior to performing any unmanaged cryptographic actions. If the incoming sender's `PeerId` is missing from the local group roster or marked as evicted, the message payload must be dropped immediately, preventing unmanaged memory allocation or decryption thrashing from unauthorized network elements.
    2. Decrypt: Call `ISenderKeyCryptographyService.Decrypt(...)` (This remains parallelizable across threads with no lock required).
    3. Persist: Call `IChatMessageWriter.AddGroupMessageAsync(...)`.
    4. Fan-Out: Call `IGroupNotificationDispatcher.DispatchAsync(...)`.

4. gRPC Streaming Service Anchor (`Percolator.Infrastructure/Network/Grpc/PercolatorMessageService.cs`)
* Add the following endpoint contract to `PercolatorMessageService`:
  ```csharp
  public override async Task StreamGroupMessages(
      GroupStreamRequest request, 
      IServerStreamWriter<GroupStreamResponse> responseStream, 
      ServerCallContext context)
  ```
* **Logic:** Parse the incoming group identifier into a `Percolator.Application.Chat.ConversationId` and the sender metadata into a `Percolator.Application.Chat.PeerId`. Call `_dispatcher.RegisterStream(conversationId, peerId, responseStream)`. Keep the stream alive using a processing loop bounded by `while (!context.CancellationToken.IsCancellationRequested) { await Task.Delay(1000, context.CancellationToken); }`. Upon exit or cancellation, safely execute `_dispatcher.UnregisterStream(conversationId, peerId)`.

**Testing Requirements (Chunk 6):**
- `GrpcGroupNotificationDispatcher_DispatchAsync_FansOutPayloadToAllRegisteredWriters_WhenConversationHasMultipleActiveStreams` - Test that DispatchAsync fans out the payload to all registered IServerStreamWriter instances when the conversation has multiple active streams.


---

## Chunk 7
### Feature Implementation Request: Signal Protocol Chunk 7 (Client-Side Speculative Rebase Coordinator)
You are to implement Chunk 7 of our Signal Protocol Group V2 integration for Percolator, isolating client-side conflict resolution behind a reusable Process Manager.

Architectural Constraints (CRITICAL):
* **No Dirty Memory States:** Do not apply state mutations directly to tracked repository entities before formal network confirmation. Speculative mutations must be verified cleanly without dirtying live cache entities.
* **Reusable Coordination Over Indirection:** Do not write custom retry loops or network synchronization blocks inside individual handlers. Centralize this orchestration within an application-layer Process Manager (`GroupMutationCoordinator`).
* **Intent-Based Validation via CQRS:** Group mutations must be modeled as structural proposals so they can be re-evaluated for validity if the group baseline shifts during a sync catch-up execution loop.

Implementation Requirements
1. The Proposal Model & Domain Safeguard (`Percolator.Chat`)
* Define an interface for mutations locally: `IGroupMutationProposal`.
* Implement an explicit proposal record: `RenameGroupProposal(string NewName) : IGroupMutationProposal`.
* Update the `GroupConversation` aggregate root to support deep copying or dry validation:
  `public bool EvaluateProposal(IGroupMutationProposal proposal, out string? businessRuleViolation)`

2. The Mutation Coordinator Process Manager (`Percolator.Application/Apps/Chat`)
* Create a centralized service orchestrator: `GroupMutationCoordinator`.
* **Isolation Boundary Control:** The `GroupMutationCoordinator` handles the isolation loop cleanly without passing leaked unmanaged cryptographic tokens through public handler boundaries. All cryptographic operations remain contained within their respective domain service contexts.
* **Method Signature:**
  ```csharp
  Task<MutationResult> CoordinateMutationAsync(
      Percolator.Application.Chat.ConversationId conversationId, 
      IGroupMutationProposal proposal, 
      SelfId selfIdentityId, 
      CancellationToken ct)
  ```
* **The Core Loop Engine:**
    * Establish a strict retry limit loop (maximum 3 attempts).
    * **Step 1:** Load a completely fresh instance of the aggregate from `IGroupConversationRepository`.
    * **Step 2:** Execute `group.EvaluateProposal(proposal, out var error)`. If it fails validation due to a state change found during catch-up, abort instantly and return `MutationResult.Failed(error)`.
    * **Step 3:** Serialize the proposal intent to an encrypted payload using the current aggregate epoch context.
    * **Step 4:** Dispatch the frame to `IRelayClient.PublishGroupMutationAsync(...)`.
    * **Step 5 (On Success):** Now that consensus is won, apply the mutation directly to the domain object, commit it locally using `_repository.UpdateAsync(...)`, and return success.
    * **Step 6 (On Conflict):** Call `_relayClient.FetchMissingEpochsAsync(...)`. Pass the returned delta payload directly to `IGroupSyncService.FastForwardLocalStateAsync(...)` to advance the baseline SQLite database. Yield thread execution to the next iteration loop.

3. Refactored Application Handlers (`Percolator.Application/Apps/Chat`)
* Refactor `UpdateGroupInfoHandler` to be completely lean. It should simply instantiate a `RenameGroupProposal`, pass it directly to the `GroupMutationCoordinator`, and evaluate the returned structural outcome.

**Testing Requirements (Chunk 7):**
- `GroupMutationCoordinator_CoordinateMutation_AbortsImmediately_WhenLocalProposalFailsBusinessRules` - Test that CoordinateMutationAsync aborts immediately when the local proposal fails business rule validation.
- `GroupMutationCoordinator_CoordinateMutation_RetriesExactlyThreeTimes_WhenEncounteringContinuousEpochConflicts` - Test that CoordinateMutationAsync retries exactly three times when encountering continuous epoch conflicts.


---

## Chunk 8
### Feature Implementation Request: Signal Protocol Chunk 8 (P2P Relay Opt-In & R3 State Engine)
You are to implement Chunk 8 of our Signal Protocol integration for Percolator, allowing client nodes to dynamically opt-in to hosting a blind group relay and managing the network state via an R3-powered WPF state service.

Architectural Constraints (CRITICAL):
* **Single-Host Capability Toggle:** `IGrpcServerManager` enforces a single host instance per node and must not be stopped or restarted during runtime. `IRelayHostingAppService.SetRelayStateAsync(bool enable, CancellationToken ct)` must simply toggle a fast, cached capability flag.
* **Early-Gate Enforcement:** The gRPC endpoints inside `PercolatorMessageService` must evaluate this local capability flag *at the absolute entry point of the call stack*. If hosting is disabled, immediately throw an `RpcException(StatusCode.PermissionDenied)` before performing any cryptographic allocations, unmanaged FFI contexts, or Zero-Knowledge verifications.
* **Direct Service Invocation:** The WPF `RelayStateService` must invoke the Application service interface directly to prevent MediatR overhead for infrastructure lifecycle toggles.
* **Query/Command Separation (CQRS):** For network-routing path resolution, do not use heavy domain repositories. Introduce an optimized, read-only `IGroupRoutingQueries` interface returning lightweight primitive value types for fast routing lookups.

Implementation Requirements
1. Application & Persistence Layer (`Percolator.Application/Chat` & `Percolator.Infrastructure/Chat`)
* The Service Contract: Define `IRelayHostingAppService` in `Percolator.Application/Chat`.
* The Routing Query: Define `public interface IGroupRoutingQueries { Task<Percolator.Application.Chat.PeerId?> GetDesignatedRelayAsync(Percolator.Application.Chat.ConversationId conversationId, CancellationToken ct); }` in `Percolator.Application/Chat`.
* The Schema: Create a `GroupRelayMappingDbo` containing `Guid ConversationId` (PK), `Guid RelayPeerId`, `DateTimeOffset LastAssignedUtc` inside `Percolator.Infrastructure/Chat/Persistence`. Implement a lightweight, no-tracking (`AsNoTracking()`) execution path for `IGroupRoutingQueries` against this table in `Percolator.Infrastructure/Chat`.

2. The Presentation State Plane (`Desktop.Wpf/Features/Simulator`)
* The State Service: Create `RelayStateService` as an application singleton.
    * Inject `IRelayHostingAppService` directly.
    * Use R3's `ReactiveProperty<bool>` and debounced `Subject<bool>.Chunk()` processing loops to sequentially execute `_relayHostingAppService.SetRelayStateAsync(finalIntent, ct)` to guard against configuration thrashing.
* The DI Registration: Register `IRelayStateService, RelayStateService` as a Singleton in `App.xaml.cs`.

```csharp
public sealed class RelayStateService : IRelayStateService, IDisposable
{
    private readonly ReactiveProperty<bool> _isRelayRunning = new(false);
    private readonly Subject<bool> _toggleSubject = new();
    private readonly DisposableBag _bag = new();

    public ReadOnlyReactiveProperty<bool> IsRelayRunning => _isRelayRunning;

    public RelayStateService(IRelayHostingAppService relayHostingAppService, TimeProvider timeProvider)
    {
        _isRelayRunning.AddTo(ref _bag);
        _toggleSubject.AddTo(ref _bag);

        _toggleSubject
            .Chunk(TimeSpan.FromMilliseconds(300), timeProvider)
            .Where(toggles => toggles.Length > 0)
            .SubscribeAwait(async (toggles, ct) => 
            {
                bool finalIntent = toggles[^1];
                await relayHostingAppService.SetRelayStateAsync(finalIntent, ct);
            }, AwaitOperation.Sequential)
            .AddTo(ref _bag);
    }

    public void RequestToggle(bool enable) => _toggleSubject.OnNext(enable);
    public void UpdateRunningState(bool running) => _isRelayRunning.Value = running;
    public void Dispose() => _bag.Dispose();
}
```

* **The ViewModel:** Update `ShellViewModel` to inject `RelayStateService`. Expose:
    * `public BindableReactiveProperty<bool> IsRelayEnabled { get; }`
    * `public AsyncRelayCommand ToggleRelayCommand { get; }`
    * Bind `IsRelayEnabled` directly to the state service property using `.ToBindableReactiveProperty()`.

3. Infrastructure Service Implementation (`Percolator.Infrastructure/Chat`)
* Implement `RelayCapabilityManager` implementing `IRelayHostingAppService`.
* **Logic:** `SetRelayStateAsync(bool enable, CancellationToken ct)` modifies a persisted capability toggle or thread-safe state container. Update the endpoints implemented in Chunk 4 (`PublishGroupMessage`) to verify this local state flag at the absolute entry gate of the gRPC request before entering any cryptographic or FFI verification paths.

**Testing Requirements (Chunk 8):**
- `RelayStateService_RequestToggle_BatchesRapidUserInputs_AndInvokesAppServiceOnlyWithFinalIntent` - Test that RelayStateService batches rapid user inputs and invokes the app service only with the final intent.


---
## Chunk 9
### Feature Implementation Request: Signal Protocol Chunk 9 (WPF MVVM Presentation Layer)
You are to implement Chunk 9 of our Signal Protocol Group V2 integration for Percolator, surfacing Group Creation, Invitation Management, and Relay Host Controls.

Architectural Constraints (CRITICAL):
* **Zero Database Leakage in Presentation:** ViewModels must have zero awareness of EF Core, SQL parameters, or database entity objects (`PendingGroupInvitationDbo`). They must bind exclusively to Application-driven read models.
* **Direct Application Services:** Presentation components must manipulate business state through explicit, focused interface methods on an Application service (`IGroupInvitationAppService`), completely avoiding MediatR dispatching overhead for single-consumer UI interactions.
* **Nested Observable Projections:** The UI state service must expose an `IReadOnlyObservableList<PendingInviteModel>` where individual model items contain their own granular, mutable R3 `ReactiveProperty<T>` states. This allows the WPF UI to perform atomic property-level updates without forcing a complete collection view redraw.

Implementation Requirements
1. Multi-Select Roster & Group Creation Dialog (`Desktop.Wpf`)
* **The Wrapper Model:** Create `SelectablePeerItemViewModel`. It wraps `PeerConnectionModel` and adds a `BindableReactiveProperty<bool> IsSelected`.
* **The Dialog ViewModel:** Create `CreateGroupDialogViewModel`.
    * Properties: `BindableReactiveProperty<string> GroupName`, `ObservableList<SelectablePeerItemViewModel> SelectablePeers`.
    * Behavior: On execution, filter out selected peers, extract their strongly-typed identifiers, and invoke a direct call to the Application layer to create the group conversation. Close the window upon completion via `IWindowManager` logic.

2. Group Invitation Management UI (`Desktop.Wpf` & `Percolator.Application`)
* **The Reactive Model:** Define `PendingInviteModel` in the Application layer, exposing a `ReactiveProperty<InviteStatus>` field.
* **The App Service:** Create `IGroupInvitationAppService` with `Task AcceptAsync(Percolator.Application.Chat.ConversationId conversationId, CancellationToken ct)` and `Task IgnoreAsync(Percolator.Application.Chat.ConversationId conversationId, CancellationToken ct)`.
* **The Menu ViewModel:** Create `GroupInvitesMenuViewModel` projecting an `ISynchronizedView` from the State Service's observable list of `PendingInviteModel`s.
    * Bind interaction buttons directly to your App Service execution tasks.
    * Connect the live list element count to the custom `MatButton.NotificationCount` badge layout on the sidebar framework.

3. Relay Host Settings Panel (`Desktop.Wpf`)
* Inject the singleton `RelayStateService` into the relevant Settings ViewModel.
* Declare a `public BindableReactiveProperty<bool> HostRelaySwitch { get; }` property connected via `.ToBindableReactiveProperty()`.
* Render a `MatSlideToggle` control in XAML bound directly to this switcher, routing toggles safely through the debouncer.
---

## Chunk 10
### Feature Implementation Request: Signal Protocol Chunk 10 (The Lightweight Group Simulator)
You are to implement Chunk 10 of our Signal Protocol Group V2 integration for Percolator. This chunk extends the existing in-memory simulator to test Group Creation, Invites, and Messaging against the Main Application, strictly avoiding Smart UI anti-patterns.

Architectural Constraints (CRITICAL):
* **Smart UI Prevention:** ViewModels must NOT contain FFI logic, Protobuf serialization, or key generation. They must delegate entirely to an Application-layer orchestrator.
* **State/Behavior Separation:** `SimulatedPeerModel` must remain a lightweight, state-only mock object. Do not embed protocol execution logic inside the model itself.
* **Direct Interception Routing:** Leverage the existing `SimulatorOutboundInterceptor` and `SimulatorToMainTransportService` to pass `ChatEnvelope` envelopes back and forth smoothly.

Implementation Requirements
1. Simulated Group Crypto State (`Desktop.Wpf/Features/Simulator`)
* Extend the existing `SimulatedPeerModel` layout:
    * Add `public Dictionary<Percolator.Chat.ConversationId, Percolator.Cryptography.GroupMasterKey> GroupMasterKeysMutable { get; } = new();`.
* **Cross-Domain Mapping Note:** The mock simulator layer explicitly maps across both domain contexts for protocol scripting, using `Percolator.Chat.ConversationId` for conversation identification and `Percolator.Cryptography.GroupMasterKey` for cryptographic state resolution.

2. The Simulator Orchestrator (`Desktop.Wpf/Features/Simulator`)
* Create `ISimulatedGroupOrchestrator` and its concrete implementation. This is the core workflow engine that drives the mock protocol script.
* **Methods:**
    * `Task HandleIngressAsync(SimulatedPeerModel peer, ChatEnvelope envelope)`:
        * If it's a `GroupInvite`, extract the `GroupMasterKey`, save it to the peer's `GroupMasterKeysMutable` directory, and process the `SenderKeyDistributionMessage` via `SignalCrypto` FFI utilities.
        * If it's a `GroupMessage`, decrypt it via the unmanaged FFI layer and log the output stream directly to the simulator's diagnostic panel.
    * `Task CreateGroupWithMainAsync(SimulatedPeerModel initiator)`:
        * Generate a `Percolator.Cryptography.GroupMasterKey`, generate the distribution message, package it into a `GroupInvite` Protobuf payload, and dispatch via `SimulatorToMainTransportService`.
    * `Task SendGroupMessageAsync(SimulatedPeerModel sender, Percolator.Chat.ConversationId conversationId, string message)`:
        * Encrypt the string using the mock peer's `SenderKey`, package the Protobuf data frame, and dispatch via the transport service.

3. Simulator Ingress Wiring (Main -> Simulator)
* Update `SimulatorStateService.ReceiveOpaqueMessageFromMainAsync`.
* After decrypting an incoming 1:1 `ChatEnvelope`, check if it contains Group V2 payloads (Invites or Group Messages). If so, immediately hand the execution channel off to `await _groupOrchestrator.HandleIngressAsync(peer, envelope)`.

4. Simulator Egress & Presentation (`Desktop.Wpf/Features/Simulator`)
* Update `SimulatedPeerCardViewModel` to include two new, clean macro commands:
    * `AsyncRelayCommand CreateGroupWithMainCommand`: Calls `await _groupOrchestrator.CreateGroupWithMainAsync(_peerModel)`.
    * `AsyncRelayCommand SendGroupMessageCommand`: Calls `await _groupOrchestrator.SendGroupMessageAsync(_peerModel, selectedConversationId, testMessage)`.
* Update `SimulatedPeerCardView.xaml` to surface these two commands as standard `MatButton` controls.
---