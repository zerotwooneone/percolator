
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
## Chunk 5 ✅ COMPLETE
**Group provisioning through outbox**
This chunk refactored the identity system to use PublicIdentityId (Guid) as the global identifier and PeerId/SelfId (uint) as local database surrogate keys. It addressed incorrect BitConverter conversions, updated network contracts to use PublicIdentityId instead of PKH for routing, and fixed identity resolution logic throughout the codebase.
---
### Chunk 5.1 
The simulator and protocol currently rely heavily on `IdentityPublicKeyHash` (PKH) for over-the-wire routing and identity resolution, whereas Signal uses Service IDs (UUIDs/PNIs) for these operations. `PublicIdentityId` was added to several handshake messages recently but has not yet permeated the routing, queueing, and application layers.

Here is the step-by-step plan to transition the simulator, protocol, and main application entirely to `PublicIdentityId`.

**1. Protocol Updates (`internal_messaging.proto`)**
The protocol itself dictates PKH usage for routing and addressing. 
*   **Message Queueing:** In `EnqueueOpaqueMessageRequest`, replace `recipient_public_key_hash` with `recipient_public_identity_id` (bytes).
*   **Pre-key Submission:** In `SubmitPreKeyBundleRequest`, add `optional bytes public_identity_id` so the relay knows which UUID to index the bundle under.
*   **Pre-key Fetching:** In `GetPreKeyBundleRequest`, replace `public_key_hash` with `public_identity_id` (bytes).
*   **Chat 1:1 Routing:** In `TextMessage`, `ReadReceipt`, `EmojiAnnotation`, and `DeliveredReceipt`, replace `optional bytes public_key_hash` with `optional bytes public_identity_id` (bytes).

**2. Main Application Updates (`Percolator.Application` & `Percolator.Infrastructure`)**
The main application uses PKH for identity resolution, routing, and sealed sender authentication.
*   **Commands:** Update `InitiateHandshakeViaHostCommand` to use `TargetPublicIdentityId` instead of `TargetPublicKeyHash`.
*   **Identity Resolution:** In `IPeerIdentityQueries` and its implementations, replace methods like `GetPublicKeyByPkhAsync(IdentityPublicKeyHash pkh)` with `GetPublicKeyByPublicIdentityIdAsync(PublicIdentityId id)`. Shift network ingress resolution to `GetByPublicIdentityIdAsync` and deprecate `FindByPublicKeyHashAsync`.
*   **Routing Profiles:** Ensure `PeerRoutingProfile` and related tables use `PublicIdentityId` as the unique identifier for routing lookups over the wire instead of PKH.
*   **Sealed Sender Authentication:** In `PeerAuthenticationService` and related tests (`AuthenticateDeliveryCertificateRequestAsync`), replace `senderPkh` with `senderPublicIdentityId` in the method signature and the signed payload format (i.e. `{senderPublicIdentityId}{timestamp}`).
*   **Message Handlers:** Update `ProcessInternalEnvelopeHandler` and `DeliverOpaqueMessageHandler` to extract and honor `public_identity_id` when parsing chat envelopes or communicating with the MQ service and DHT.
*   **PreKey and DHT Updates:** Update `GetPreKeyBundleQuery`, `GetPreKeyBundleHandler`, DHT node logging, and underlying PreKey stores to resolve bundles by mapping the incoming `PublicIdentityId` to the surrogate `PeerId`, dropping the PKH lookup logic.
*   **Database Migrations:** Create an EF Core migration to alter `MessageQueueItemDbo` (the `MessageQueue` table). Drop `RecipientPkh` and replace it with `RecipientPublicIdentityId`. (`PreKeyBundleDbo` and `PendingSessionDbo` are safe as they already use the `PeerId` surrogate key).

**3. Simulator Persistence & DTO Updates (`SimulatorState.cs` & `JsonSimulatorStateRepository.cs`)**
The simulator persists relay queues and pending handshakes using PKH. These must be migrated to persist and identify by UUID.
*   **Pending Handshakes:** In `StandardSignalStoreDto`, rename `PendingHandshakeToMainResponderPublicKeyHash` to `PendingHandshakeToMainResponderPublicIdentityId` (and update `SimulatedPeerModel` accordingly).
*   **Relay Queues:** In `RelayQueuedBlobDto` and `InboundRelayMessageSnapshot`, replace `RecipientRoutingKey` / `TargetIdentityPublicKeyHash` with `TargetPublicIdentityId`.
*   **Pre-key Store:** In `PublishedPreKeyBundleDto`, replace `RecipientPublicKeyHash` with `RecipientPublicIdentityId`.
*   **Routing State:** In `SimulatedPeerDto` and `SimulatedPeerModel`, replace `TargetPublicKeyHash` with `TargetPublicIdentityId`.

**4. Simulator Core Services Updates (`ISimulatorStateService.cs`)**
The API surface of the simulator dictates PKH routing for queues and standard signal handshakes.
*   **Lookups:** Replace `TryGetPeerIdByIdentityPublicKeyHashAsync` with `TryGetPeerIdByPublicIdentityIdAsync(PublicIdentityId id)`.
*   **Relay Queuing Methods:** Update the signature of `EnqueueRelayDownstreamToPeerAsync` and `DequeueRelayDownstreamToPeerAsync` to take `PublicIdentityId targetPublicIdentityId` instead of `IdentityPublicKeyHash targetIdentityPublicKeyHash`.
*   **Handshakes:** Rename and update `InitiateStandardHandshakeToMainByRelayPkhAsync` to `InitiateStandardHandshakeToMainByRelayPublicIdentityIdAsync`.
*   **Acceptance:** Update `TryAcceptPendingStandardSignalHelloAsync` to accept `initiatorPublicIdentityId` rather than `initiatorPkhHex`.

**5. Simulator Application Logic & UI Updates (`SimulatorStateService.cs` & Desktop UI)**
The implementations and UI bindings must be updated to route envelopes using the new protocol fields.
*   **WPF UI Bindings:** Update `SimulatorRelayTabViewModel`, `SimulatorSessionsTabViewModel`, `SimulatedPeerItemViewModel`, and their associated `.xaml` files to bind to `TargetPublicIdentityId` instead of `TargetPublicKeyHash`.
*   **Handling `EnqueueOpaqueMessageRequest`:** Update the logic to read `RecipientPublicIdentityId` instead of throwing if `recipient_public_key_hash` is missing, and pass it to the updated `EnqueueRelayDownstreamToPeerAsync`.
*   **Handling `GetPreKeyBundleRequest`:** Look up pre-keys from the simulated relay's `PreKeyStore` using `PublicIdentityId` rather than matching PKH.
*   **Message Dispatch:** Any simulated peer looking to deliver messages via relay or direct fallback should attach the target's `PublicIdentityId` to the outgoing request rather than hashing the SPKI.

**Summary & Constraints:**
*   `PeerId` (the `uint`) is correctly staying completely local to the in-memory maps (`_peerById` and `RemoteNetworkPeerId` inside `SecureSession`) and local database relations.
*   **Safety Number Constraint:** Do NOT delete `IdentityPublicKeyHash` computation logic. It must be retained locally for Safety Number generation and UI verification. We are only scrubbing PKH from network transport, serialized DTOs, and routing queues.
*   **Cryptographic Integrity:** `initiator_identity_key_spki` and `AcceptorIdentityKey` MUST remain intact in all handshake envelopes. The X3DH layer must continue to perform its DH math using the raw Curve25519/Ed25519 keys, while the outer routing and local DB lookups pivot to use the `PublicIdentityId` included in the same envelope. To complete the refactor, trust the `PublicIdentityId` (UUID) as the sole wire identifier across the protocol, main application, and simulator for routing.
---
## Chunk 6
### Feature Implementation Request: Signal Protocol Chunk 6 (The Streaming Data Plane)
You are to implement the high-velocity, real-time Data Plane for Group V2 messaging, cleanly separating the Relay's opaque fan-out responsibilities from the Client's local decryption and persistence logic.

Architectural Constraints (CRITICAL):
* **Strict E2EE Separation:** The Relay role must *never* decrypt payloads or touch application models. It only routes raw `ciphertext` to connected streams. The Client role performs decryption locally *after* receiving the stream event.
* **Clean Architecture Directional Dependency:** The Relay's streaming dispatcher is a purely infrastructural networking concern. The Application layer must have zero knowledge of it.
* **Shared Nothing / Thread Safety:** `IServerStreamWriter` is strictly NOT thread-safe. You must serialize concurrent writes to individual peer streams using isolated asynchronous queues (e.g., `System.Threading.Channels.Channel<GroupStreamResponse>`).
* **DDD & Signal Protocol Validation:** The Client Ingress must load the local `GroupConversation` aggregate to validate membership *and* verify the incoming message's epoch against the local ledger before attempting decryption.

Implementation Requirements

1. Protobuf Updates (`messaging.proto`)
* Add `GroupStreamRequest` message:
  ```protobuf
  message GroupStreamRequest {
      optional bytes conversation_id = 1;
  }
  ```
* Add `GroupStreamResponse` message:
  ```protobuf
  message GroupStreamResponse {
      optional bytes conversation_id = 1;
      optional bytes ciphertext = 2;
      optional uint32 epoch = 3;
      optional bytes sender_public_identity_id = 4;
  }
  ```
* Add RPC to `RelayGroupService`: `rpc StreamGroupMessages(GroupStreamRequest) returns (stream GroupStreamResponse);`

2. Relay Fan-Out Infrastructure (`Percolator.Infrastructure` ONLY)
* Define interface and implementation entirely within `Percolator.Infrastructure/Network/Grpc`: `internal interface IRelayGroupStreamDispatcher { Task DispatchAsync(Guid conversationId, ReadOnlyMemory<byte> ciphertext, uint epoch, Guid senderPublicIdentityId, CancellationToken ct); }`
* Create `GrpcRelayGroupStreamDispatcher`.
    * **Storage Matrix:** `ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<GroupStreamResponse>>>` (ConversationId -> PublicIdentityId -> Channel).
    * Implement `DispatchAsync(...)`: Extract the nested list of channels for the `conversationId`. For each channel, use `ChannelWriter.TryWrite(...)` to enqueue the payload non-blockingly.
    * Expose helpers: `ChannelReader<GroupStreamResponse> RegisterStream(Guid conversationId, Guid publicIdentityId)` and `void UnregisterStream(Guid conversationId, Guid publicIdentityId)`.
* Wire into `RelayGroupService` (in `Percolator.Infrastructure/Network/Grpc/RelayGroupService.cs`):
    * Implement `StreamGroupMessages`: Extract caller's `PublicIdentityId` from context. **Authorization Gate:** Call `_ledgerRepository` or `_orchestrator` to verify the caller's `PublicIdentityId` is a valid member of the `ConversationId`. If not, throw an `RpcException(PermissionDenied)`. Call `RegisterStream(...)` to get a `ChannelReader`. Use a `await foreach (var msg in reader.ReadAllAsync(context.CancellationToken))` loop to safely `await responseStream.WriteAsync(msg)`. In a `finally` block, call `UnregisterStream(...)`.
    * Update `Publish` method: After successful ledger validation, call `_dispatcher.DispatchAsync(...)`.

3. Client Fan-In Infrastructure (`Percolator.Infrastructure`)
* The plan must include a client-side stream consumer to pump messages into the application layer. Create `RelayGroupStreamWorker` (an `IHostedService` or background loop in `Percolator.Infrastructure`) that connects to `RelayGroupService.StreamGroupMessages` for active groups.
* The worker simply loops over `ResponseStream.ReadAllAsync()` and passes the payload to `IGroupStreamIngressProcessor.ProcessGroupMessageAsync`.

4. Client Ingress Orchestration (`Percolator.Application` & `Percolator.Chat`)
* Update `IChatMessageWriter` (in `Percolator.Chat/Messaging/App/IChatMessageWriter.cs`): Add `Task AddGroupMessageAsync(Percolator.Chat.Messaging.ValueObjects.ConversationId conversationId, Percolator.Chat.GroupMembership.ParticipantId senderId, string content, Percolator.Chat.Messaging.ValueObjects.PublicMessageId publicMessageId, DateTimeOffset sentAt, CancellationToken cancellationToken);`
* Update `SqliteChatMessageWriter` (in `Percolator.Infrastructure/Chat/SqliteChatMessageWriter.cs`) to implement `AddGroupMessageAsync` using standard EF Core entity appends.
* Create `IGroupStreamIngressProcessor` and its implementation `GroupStreamIngressProcessor` in `Percolator.Application/Apps/Chat`.
    * Implement `ProcessGroupMessageAsync(Guid conversationIdBytes, Guid senderPublicIdentityIdBytes, uint epoch, byte[] ciphertext, CancellationToken ct)`.
    * **DDD Validation:** Load the group aggregate (via `IGroupConversationRepository` or an optimized read model). Verify the sender's `PeerId` is an active member. If not, drop or reject the message.
    * **Epoch Verification:** Compare the incoming `epoch` against the local group's current epoch. If `incoming > local_epoch`, throw an exception or return a result indicating a sync is required (do not attempt decryption).
    * **Decrypt:** Call `ISenderKeyCryptographyService.DecryptGroupMessage(...)` to obtain the plaintext (use `new DeviceId(1)` for the sender device).
    * **Persist:** Call `IChatMessageWriter.AddGroupMessageAsync(...)` to save the decrypted message locally.

**Testing Requirements (Chunk 6):**
- `GrpcRelayGroupStreamDispatcher_DispatchAsync_WritesToAllChannels_WhenConversationHasMultipleActiveStreams` - Test that DispatchAsync enqueues the payload to all registered Channel instances when the conversation has multiple active streams.


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