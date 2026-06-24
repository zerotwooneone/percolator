
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

The Goal: Alice creates a group and designates a Relay Peer. She persists the state atomically, creating outbox messages to (1) Provision the group on the Relay (uploading the zero-knowledge public params and member PKHs), and (2) Dispatch 1:1 invites to members containing the master key and relay coordinates.

Architectural Constraints (CRITICAL):
* **P2P Relay Alignment (Signal zkgroup):** To approximate Signal's Group V2 in a P2P environment, the chosen Relay acts as the "Server". The Relay must be provisioned with `GroupPublicParams` and a list of member Public Key Hashes (PKHs) for routing. The Relay MUST NOT receive the `GroupMasterKey` or any plaintext metadata, preserving Sealed Sender and blind roster management.
* **Identity Decoupling (The Composite Identity Pattern):** `PeerId` is a local-only database concept and must never be assumed to exist globally. When a peer (or relay) receives instructions to join/provision a group, they may not have a 1:1 session with all members. Model this cleanly using a **Composite Identity Value Object** (e.g., `GroupParticipantId(Pkh Pkh, ChatPeerId? LocalPeerId)`) where the 32-byte `Pkh` is the authoritative global identifier (always present) and `LocalPeerId` is an optional local surrogate key. Update `GroupMember` to key its membership on this composite identity.
* **Zero Naked Bytes:** Avoid raw `byte[]` for routing identities. Use the existing `Percolator.Chat.Messaging.ValueObjects.Pkh` `[ByteArray]` wrapper across all Application, Domain, and Infrastructure boundaries (leveraging EF Core Value Converters on DBOs).
* **Strict Domain Isolation & Zero-Allocation Translation:** `Percolator.Chat`, `Percolator.Identity`, and `Percolator.Cryptography` do not reference each other. Types needed in multiple domains (like the distribution message or public params) must be duplicated in each domain. The Application layer orchestrates between them using zero-allocation span conversions: `ChatDomainType.FromSpan(cryptoDomainType.Span)`. When parsing Protobuf `ByteString`, use `DomainType.FromBytesOwned(byteString.ToByteArray())` to take ownership of the defensive copy.
* **Explicit Persistence:** Do not implement a complex EF Core DbContext Interceptor. The Application layer orchestrates saving by calling `await _groupConversationRepository.AddWithOutboxAsync(group, ct)`. The infrastructure implementation maps raised domain events to the outbox table inside a single atomic `SaveChangesAsync` call.

Implementation Requirements
1. The Cryptography Domain (`Percolator.Cryptography`)
* **Missing Primitives & Services:** Define `[ByteArray(minLength: 1, maxLength: 5000)] public sealed partial record SenderKeyDistributionMessageBytes;`.
* Add `ZkGroupPublicParamsBytes DeriveGroupPublicParams(GroupMasterKey masterKey);` to `IGroupCryptographyService`.
* Define `ISenderKeyCryptographyService` with a method to generate the sender key distribution message, e.g., `SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(Percolator.Cryptography.Primitives.ConversationId conversationId, Percolator.Cryptography.Primitives.PeerId localPeerId, Percolator.Cryptography.Primitives.DeviceId deviceId)`.

2. Domain Layer (`Percolator.Chat`)
* **Local Primitives:** Define `[ByteArray(minLength: 1, maxLength: 5000)] public sealed partial record ChatSenderKeyDistributionMessageBytes;` inside `Percolator.Chat.Messaging.ValueObjects` so the Chat domain doesn't depend on Cryptography.
* **Composite Identity Value Object:** Define `public sealed record GroupParticipantId(Pkh Pkh, ChatPeerId? LocalPeerId);`.
* **GroupConversation & GroupMember Refactor:**
    * Change `GroupMember` to key its membership on `GroupParticipantId` instead of `ChatPeerId`. Add `void ResolveLocalPeerId(ChatPeerId localId)` to upgrade an unresolved member.
    * Add `GroupParticipantId RelayIdentity` to the `GroupConversation` aggregate root. The group MUST know the globally verifiable routing identity of its host.
    * Add an internal `_domainEvents` collection and `void ClearDomainEvents()`.
    * Update creation logic to accept the `RelayIdentity` and register a `GroupProvisioningRequestedDomainEvent` containing the `RelayGroupPublicParamsBytes` (already defined in Chat domain) and roster `Pkh`s.
    * Update `InviteMember` to register a `MemberInvitedDomainEvent` containing the local `ChatSenderKeyDistributionMessageBytes` and the Relay's `Pkh`.

3. Message Queue Decoupling (`Percolator.Infrastructure`)
* **Infrastructure Bug Fix:** `MessageQueueItemDbo` currently uses `PeerId RecipientPeerId`. This strictly limits the queue to known local peers. You MUST refactor `MessageQueueItemDbo` to use `Pkh RecipientPkh` directly, registering an EF Core Value Converter in `PercolatorDbContext` to map it to a byte array. Update `SqliteMessageQueueRepository` and `internal_messaging.proto` `EnqueueOpaqueMessageRequest` bindings to reflect this fully-typed PKH-based routing.

4. Relay Provisioning Ingress (`Percolator.Infrastructure/Services/RelayGroupService.cs`)
* **The Provisioning Endpoint:** The Relay must expose a way to be provisioned blindly. Add `rpc ProvisionGroup(ProvisionGroupRequest) returns (ProvisionGroupResponse);` to `messaging.proto`.
* **Implementation:** `RelayGroupService.ProvisionGroup` receives the `ConversationId`, `ZkGroupPublicParams`, and member PKHs. It creates a `RelayGroupStateDbo` (Epoch 0) and populates `RelayBlindedRosterDbo` using a converted `Pkh MemberPkh` instead of a local `Guid`.

5. Infrastructure Layer (`Percolator.Infrastructure/Chat/Persistence`)
* **RelayOutboxDbo:** Create a DBO to store pending domain events: `Guid Id`, `string EventType`, `string PayloadJson`, `Pkh DestinationPkh`, `DateTimeOffset? ProcessedAtUtc`. Use an EF Core Value Converter for `DestinationPkh`. 
* **IGroupConversationRepository Update:** Implement `Task AddWithOutboxAsync(GroupConversation conversation, int selfIdentityId, CancellationToken ct)`.
* **OutboxDispatcherWorker:** An `IHostedService` that queries unprocessed events. 
  * If it's a `GroupProvisioningRequestedDomainEvent`, it invokes the Relay's `ProvisionGroup` gRPC endpoint via a transport client.
  * If it's a `MemberInvitedDomainEvent`, it dispatches the `GroupInvite` via `IRemoteEnvelopeSender`.
* **Transient Network Backoff:** Encapsulate transient network exceptions (e.g., gRPC timeouts). If a peer is offline, log a warning, delay sequentially, and skip updating `ProcessedAtUtc`.

6. Application Orchestration (`Percolator.Application/Apps/Chat`)
* **Eliminate MediatR Indirection:** Since group creation is triggered from a single UI entry point, MediatR is unnecessary overhead. Delete the existing `CreateGroupCommand.cs` and `CreateGroupCommandHandler.cs`.
* **IGroupProvisioningAppService:** Create a new application service interface `IGroupProvisioningAppService` with a method `Task<ConversationId> ProvisionGroupAsync(string groupName, IReadOnlyList<Pkh> initialMembers, Pkh relayPkh, int selfIdentityId, CancellationToken ct);` and its implementation.
* **ProvisionGroupAsync Execution:**
    * Generate `GroupMasterKey`, derive `ZkGroupPublicParamsBytes` (Cryptography types). Convert to Chat domain using `GroupMasterKeyBytes.FromSpan(masterKey.Span)` and `RelayGroupPublicParamsBytes.FromSpan(zkParams.Span)`.
    * Save the master key via `_groupCryptoStateRepository.UpsertGroupMasterKeyAsync`.
    * Instantiate `GroupConversation` domain entity.
    * For each member, generate `SenderKeyDistributionMessageBytes` via `ISenderKeyCryptographyService`. Convert it to the Chat domain using `ChatSenderKeyDistributionMessageBytes.FromSpan(cryptoDistributionMsg.Span)`.
    * Call `group.InviteMember` using the converted Chat-domain distribution message.
    * Save atomically via `await _groupConversationRepository.AddWithOutboxAsync(group, selfIdentityId, ct)`.

7. Integration Anchor: Provisioning & Invite Ingress (`Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`)
* Update `ProcessInternalEnvelopeHandler` to include a switch case for `ChatEnvelope.MessageOneofCase.GroupInvite`. Do NOT create a new MediatR command for this; handle it directly or via a direct call to a specialized AppService.
* Upon receiving a `GroupInvite`, parse the Protobuf `ByteString` fields into proper Domain Value Objects using defensive copies: `GroupMasterKey.FromBytesOwned(invite.GroupMasterKey.ToByteArray())`, `Pkh.FromBytesOwned(invite.RelayPublicKeyHash.ToByteArray())`, etc.
* The recipient persists the `GroupMasterKey`, processes the distribution message, and initializes their local `GroupConversation` pointing to the designated Relay.

8. Network Contracts (`internal_messaging.proto` & `messaging.proto`)
* **internal_messaging.proto:** 
  * Modify `GroupInvite` message to add: `optional bytes relay_public_key_hash = 6;`.
  * Change `create_group = 16` to `group_invite = 16` in the `ChatEnvelope` oneof configuration.
* **messaging.proto:**
  * Define `ProvisionGroupRequest`: `optional bytes conversation_id = 1; optional bytes group_public_params = 2; repeated bytes member_public_key_hashes = 3;`.
  * Define `ProvisionGroupResponse`: `optional bool success = 1;`.

**Testing Requirements (Chunk 5):**
- `MessageQueueItemDbo_EnqueuesByPkh_Successfully` - Test that the message queue can accept envelopes for PKHs that do not exist in the local `PeerIdentityDbo` database.
- `GroupProvisioningAppService_ProvisionGroupAsync_GeneratesValidOutboxEvents` - Test that creating a group yields one provisioning event for the relay and one invite event per member.
- `RelayGroupService_ProvisionGroup_CreatesLedgerAndRoster_FromPkhs` - Test that the Relay correctly initializes the `RelayGroupStateDbo` and `RelayBlindedRosterDbo` using the raw PKHs.


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