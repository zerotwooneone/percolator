
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
**Group Provisioning Through Outbox**
This chunk refactored the identity system to use `PublicIdentityId` (Guid) as the global identifier and `PeerId`/`SelfId` (uint) as local database surrogate keys. It updated network contracts to use `PublicIdentityId` instead of PKH for routing, fixing identity resolution logic throughout the codebase, including the simulator.
---
## Chunk 6 ✅ COMPLETE
**The Streaming Data Plane**
This chunk implemented the real-time Data Plane for Group V2 messaging. On the Relay side, it introduced `GrpcRelayGroupStreamDispatcher` for non-blocking channel-based fan-out. On the Client side, it implemented `RelayGroupStreamWorker` and `GroupStreamIngressProcessor` to catch incoming group messages, verify the sender's membership, enforce optimistic epoch validation, and decrypt the contents locally into the SQLite database.

---
## Chunk 6.1
### Feature Implementation Request: Signal Protocol Chunk 6.1 (Architecture Correction)
You are to fix the architectural flaws introduced in Chunks 4-6 related to the Relay Ledger and Epoch mutation mechanics. In Signal Group V2, standard chat messages do NOT mutate the epoch. Only structural roster or metadata changes (which update the `EncryptedProfile`) mutate the epoch.

Architectural Constraints (CRITICAL):
* **No Epoch Advancement on Chat:** The `Publish` endpoint must verify the ZK Proof against the `CurrentEpoch` but must **never** advance the epoch or alter the `GroupPublicParams`.
* **The New Roster Contract:** The Relay tracks exact internal `PeerId`s in its `RelayBlindedRosters` table. The client sends global UUIDs (`PublicIdentityId`s) over the wire, and the Relay translates these into internal `PeerId`s (creating placeholder identities if they don't exist yet) before saving the mutation.

Implementation Requirements

1. Protobuf Updates (`Percolator.Contracts/Protos/messaging.proto`)
* Change `SubmitGroupMessageRequest`: Ensure it contains `conversation_id`, `presentation`, `ciphertext`, and `epoch`.
* Add `ModifyGroupRequest` and `ModifyGroupResponse` to `messaging.proto`:
  ```protobuf
  message ModifyGroupRequest {
      optional bytes conversation_id = 1;
      optional uint32 base_epoch = 2;
      optional bytes presentation = 3;
      optional bytes new_encrypted_profile = 4;
      repeated bytes add_public_identity_ids = 5;
      repeated bytes remove_public_identity_ids = 6;
  }

  message ModifyGroupResponse {
      optional bool success = 1;
  }
  ```
* Add `GetGroupStateRequest` and `GetGroupStateResponse` to `messaging.proto`:
  ```protobuf
  message GetGroupStateRequest {
      optional bytes conversation_id = 1;
      optional bytes presentation = 2;
  }

  message GetGroupStateResponse {
      optional uint32 current_epoch = 1;
      optional bytes public_params = 2;
      optional bytes encrypted_profile = 3;
  }
  ```
* Add the RPC endpoint `rpc GetGroupState(GetGroupStateRequest) returns (GetGroupStateResponse);` to the `RelayGroupService`.
* Add the RPC endpoint `rpc ModifyGroup(ModifyGroupRequest) returns (ModifyGroupResponse);` to the `RelayGroupService`.
* Add `bytes encrypted_profile` to `ProvisionGroupRequest`.

2. Group Encryption Profiles (`Percolator.Cryptography`)
* We must establish domain value types for the Encrypted Profile to ensure type safety between the raw bytes and the domain logic.
* In `Percolator.Chat.Messaging.ValueObjects`, define:
  ```csharp
  [ByteArray(minLength: 1, maxLength: 5000)]
  public sealed partial record EncryptedGroupProfileBytes;
  ```
* In `IGroupCryptographyService` (and its concrete implementations), add `EncryptedGroupProfileBytes EncryptGroupProfile(GroupMasterKey masterKey, ReadOnlySpan<byte> profilePlaintext);` and `byte[] DecryptGroupProfile(GroupMasterKey masterKey, EncryptedGroupProfileBytes ciphertext);`. (Use AEAD AES-GCM with a key derived from the master key).

3. The Database Updates (`Percolator.Infrastructure/Chat`)
* In `RelayGroupStateDbo`, add `public byte[] EncryptedProfile { get; set; } = Array.Empty<byte>();`.
* In `RelayBlindedRosterDbo`, remove `MemberPublicIdentityId` and replace it with `public uint MemberPeerId { get; set; }`. Add a Foreign Key relationship to `PeerIdentityDbo.PeerId` for strict referential integrity.

4. The Domain Updates (`Percolator.Chat/GroupLedger`)
* Update the `RelayGroupLedger` aggregate.
    * Add `public EncryptedGroupProfileBytes EncryptedProfile { get; private set; }`.
    * Update the constructor to take `EncryptedGroupProfileBytes`.
    * Add `public void Mutate(uint requestedEpoch, EncryptedGroupProfileBytes newProfile)`. This method asserts the new epoch is strictly greater, then updates the fields.
* Modify the `AdvanceEpoch` method to be removed or replaced, as chat messages no longer advance the epoch.
* Update `IRelayGroupLedgerRepository`:
    * Add `EncryptedGroupProfileBytes encryptedProfile` to the signature of `ProvisionNewGroupAsync` and ensure it accepts `IReadOnlyList<Percolator.Identity.PeerId>` instead of `PublicIdentityId`.
    * Add a new method: `Task UpdateGroupStateAsync(RelayGroupLedger ledger, IReadOnlyList<Percolator.Identity.PeerId> addPeerIds, IReadOnlyList<Percolator.Identity.PeerId> removePeerIds, CancellationToken cancellationToken);` which executes the ledger update and the blinded roster insertions/deletions inside a single EF Core transaction.
    * Update `IsMemberAsync` to either take a `PeerId` or perform a join against `PeerIdentityDbo` to match the target `PublicIdentityId` to the `MemberPeerId` in the roster table.
* Refactor `SqliteRelayRosterQueries.GetMemberPeerIdsAsync`: Since `RelayBlindedRosterDbo` now holds `MemberPeerId`, delete the `PeerIdentities` lookup join entirely and use the newly available `MemberPeerId` to directly look up `ChatPeerId` participants.

5. Service Implementation (`Percolator.Application/Chat` & `Percolator.Infrastructure/Network`)
* **RelayGroupOperationStatus Enum:** Define an enum `RelayGroupOperationStatus { Success, EpochConflict, Unauthorized, GroupNotFound }` in `Percolator.Application.Chat` to communicate expected domain failures without throwing exceptions.
* **RelayGroupOrchestrator:**
    * In `PublishGroupRelayMessageAsync`, change the return type to `Task<RelayGroupOperationStatus>`. **Delete** the code that advances the epoch. The method should now just verify the ZK proof against the current `GroupPublicParams`. Replace `ledger.AdvanceEpoch` with a validation check: `if (requestedEpoch != ledger.CurrentEpoch) return RelayGroupOperationStatus.EpochConflict;`. If auth fails, return `Unauthorized`. If successful, queue the message and return `Success`.
    * Add a new method: `Task<RelayGroupOperationStatus> ModifyGroupAsync(ConversationId conversationId, uint baseEpoch, ZkPresentationBytes presentation, EncryptedGroupProfileBytes newProfile, IReadOnlyList<Percolator.Chat.GroupLedger.PublicIdentityId> addPublicIdentityIds, IReadOnlyList<Percolator.Chat.GroupLedger.PublicIdentityId> removePublicIdentityIds, CancellationToken ct);`.
    * In `ModifyGroupAsync`: Return `EpochConflict`, `Unauthorized`, or `GroupNotFound` on domain failures. If valid, translate the `PublicIdentityId`s into `Percolator.Identity.PeerId`s (provisioning new ones if necessary via the Identity layer). Interact with `IRelayGroupLedgerRepository.UpdateGroupStateAsync` to persist the ledger changes alongside the insertions/deletions in the `RelayBlindedRosters` table. Finally, return `Success`.
    * Add a new method: `Task<RelayGroupLedger?> GetGroupStateAsync(ConversationId conversationId, ZkPresentationBytes presentation, CancellationToken ct);`. Verify the ZK proof before returning the ledger (or null if not found/unauthorized).
* **RelayGroupService (gRPC):**
    * Update `Publish` to evaluate the returned `RelayGroupOperationStatus` and throw the corresponding `RpcException(StatusCode.Aborted)` or `RpcException(StatusCode.Unauthenticated)` based on the enum, removing the need for domain exception catch blocks.
    * Update `ProvisionGroup` to extract and pass the `EncryptedGroupProfileBytes`.
    * Implement the new `ModifyGroup` endpoint, translating the protobuf inputs into the corresponding types for `RelayGroupOrchestrator.ModifyGroupAsync`.
    * Implement the new `GetGroupState` endpoint, calling `RelayGroupOrchestrator.GetGroupStateAsync` and returning the epoch, public params, and encrypted profile.

**Testing Requirements (Chunk 6.1):**
- `RelayGroupOrchestrator_PublishGroupRelayMessageAsync_DoesNotAdvanceEpoch` - Ensure that chat messages only verify auth and fan out, leaving the epoch unchanged.
- `RelayGroupOrchestrator_PublishGroupRelayMessageAsync_ReturnsEpochConflict_WhenEpochMismatched` - Test that providing an incorrect epoch returns the new `EpochConflict` enum instead of throwing an exception.
- `RelayGroupOrchestrator_ModifyGroupAsync_AdvancesEpochAndUpdatesProfile` - Test that the new mutation method properly increments the epoch and stores the new parameters.
- `RelayGroupOrchestrator_ModifyGroupAsync_UpdatesRoster_MappingToPeerIds` - Test that the mutation method correctly translates the PublicIdentityIds to PeerIds before passing them to the repository for blinded roster updates.

---

## Chunk 7
### Feature Implementation Request: Signal Protocol Chunk 7 (Client-Side Speculative Rebase Coordinator)
You are to implement Chunk 7 of our Signal Protocol Group V2 integration for Percolator, isolating client-side conflict resolution behind a reusable Process Manager.

Architectural Constraints (CRITICAL):
* **No Dirty Memory States:** Do not apply state mutations directly to tracked repository entities before formal network confirmation. Speculative mutations must be verified cleanly without dirtying live cache entities.
* **Reusable Coordination Over Indirection:** Do not write custom retry loops or network synchronization blocks inside individual handlers. Centralize this orchestration within an application-layer Process Manager (`GroupMutationCoordinator`).
* **Intent-Based Validation via CQRS:** Group mutations must be modeled as structural proposals so they can be re-evaluated for validity if the group baseline shifts during a sync catch-up execution loop.
* **Sealed Sender Compatibility:** The Relay is completely opaque. It tracks the roster for fan-out but does NOT know the sender of a group message. All mutations are just opaque ciphertexts published via the existing `SubmitGroupMessageRequest` gRPC endpoint, which uses ZK proofs (`presentation`) for authorization without revealing identity.

Implementation Requirements

1. Protobuf Updates (`Percolator.Contracts/Protos/internal_messaging.proto`)
* Add a `GroupUpdatePayload` to `GroupContent` to support serializing intents into the encrypted envelope.
  ```protobuf
  message GroupContent {
    optional string text_message = 1;
    optional GroupUpdatePayload update_payload = 2;
  }

  message GroupUpdatePayload {
    optional string new_group_name = 1;
    optional bytes new_avatar_id = 2;
    repeated PeerRoleUpdate role_updates = 3;
    repeated bytes added_public_identity_ids = 4;
    repeated bytes removed_public_identity_ids = 5;
  }

  message PeerRoleUpdate {
    optional bytes public_identity_id = 1;
    optional uint32 role_enum = 2;
  }

  message GroupProfilePlaintext {
    optional string group_name = 1;
    optional bytes avatar_id = 2;
    repeated PeerRoleUpdate roles = 3;
  }
  ```

2. The Proposal Model & Domain Safeguard (`Percolator.Chat`)
* Define an interface for mutations locally: `IGroupMutationProposal`.
* Implement an explicit proposal record: `RenameGroupProposal(string NewName) : IGroupMutationProposal`.
* Update the `GroupConversation` aggregate root to support deep copying or dry validation:
  `public bool EvaluateProposal(IGroupMutationProposal proposal, out string? businessRuleViolation)`
* Add `public byte[] GenerateProfilePlaintext()` to `GroupConversation` to serialize the group's current name and roles into a `GroupProfilePlaintext` protobuf payload.

3. The Mutation Coordinator Process Manager (`Percolator.Application/Apps/Chat`)
* Create a centralized service orchestrator: `GroupMutationCoordinator`.
* Define a new network interface in `Percolator.Application/Chat`: `IRelayGroupNetworkClient`. It should expose `ModifyGroupAsync`, `GetGroupStateAsync`, and `PublishGroupMessageAsync` using strictly domain types (e.g., `ZkPresentationBytes`, `EncryptedGroupProfileBytes`, `CiphertextBytes`), fully abstracting away gRPC and Protobufs.
* Inject the necessary services: `IGroupConversationRepository`, `ISelfIdentityQueries`, `IGroupCryptographyService`, `ISenderKeyCryptographyService`, and `IRelayGroupNetworkClient`.
* **Isolation Boundary Control:** The `GroupMutationCoordinator` handles the isolation loop cleanly without passing leaked unmanaged cryptographic tokens through public handler boundaries. It must have ZERO knowledge of `Percolator.Contracts` or gRPC `RpcException`s. Network errors must be wrapped in domain exceptions (e.g., `EpochConflictException`) by the infrastructure client.
* **Method Signature:**
  ```csharp
  Task<MutationResult> CoordinateMutationAsync(
      Percolator.Chat.Messaging.ValueObjects.ConversationId conversationId, 
      IGroupMutationProposal proposal, 
      CancellationToken ct)
  ```

* **The Core Loop Engine:**
    * Establish a strict retry limit loop (maximum 3 attempts).
    * **Step 1:** Load a fresh instance of the aggregate from `IGroupConversationRepository` and clone it (or evaluate it) using `EvaluateProposal`.
    * **Step 2:** If it fails validation due to a state change found during catch-up, abort instantly and return `MutationResult.Failed(error)`.
    * **Step 3 (Relay Ledger Update):** Serialize the proposed group state using `group.GenerateProfilePlaintext()`. Encrypt this using `IGroupCryptographyService.EncryptGroupProfile` to generate an `EncryptedGroupProfileBytes`. Generate a `ZkPresentationBytes` and call `_relayGroupNetworkClient.ModifyGroupAsync(...)`.
    * **Step 4 (On Conflict):** If `ModifyGroupAsync` throws an `EpochConflictException` indicating an epoch conflict or verification failure, call `_relayGroupNetworkClient.GetGroupStateAsync(...)` to fetch the authoritative latest state. Decrypt the returned `EncryptedProfile`, apply it to the local SQLite database to advance the baseline, and retry the loop.
    * **Step 5 (Fan-Out Broadcast):** Once `ModifyGroupAsync` succeeds, construct the `GroupUpdatePayload` protobuf (the diff). Encrypt it using `ISenderKeyCryptographyService.EncryptGroupMessage(...)`. Dispatch the frame to the relay using `_relayGroupNetworkClient.PublishGroupMessageAsync(...)`. 
    * **Step 6 (Commit):** Apply the mutation directly to the domain object, commit it locally using `_repository.UpdateAsync(...)`, and return success.

4. Infrastructure Network Client (`Percolator.Infrastructure/Network`)
* Implement `RelayGroupNetworkClient : IRelayGroupNetworkClient`.
* This class is responsible for injecting the gRPC `RelayGroupServiceClient`, translating domain types into `ModifyGroupRequest`, `GetGroupStateRequest`, and `SubmitGroupMessageRequest` Protobufs, executing the RPC calls, and wrapping `RpcException(StatusCode.Aborted)` into `EpochConflictException`.

5. Refactored Application Handlers (`Percolator.Application/Apps/Chat`)
* Refactor `UpdateGroupInfoHandler` to be completely lean. It should simply instantiate a `RenameGroupProposal`, pass it directly to the `GroupMutationCoordinator`, and evaluate the returned structural outcome.

**Testing Requirements (Chunk 7):**
- `GroupMutationCoordinator_CoordinateMutation_AbortsImmediately_WhenLocalProposalFailsBusinessRules` - Test that CoordinateMutationAsync aborts immediately when the local proposal fails business rule validation.
- `GroupMutationCoordinator_CoordinateMutation_RetriesExactlyThreeTimes_WhenEncounteringContinuousEpochConflicts` - Test that CoordinateMutationAsync retries exactly three times when encountering continuous gRPC RpcExceptions before giving up.

---
## Chunk 7.1
### Feature Implementation Request: Signal Protocol Chunk 7.1 (Forward Secrecy & Member Management)
You are to implement Chunk 7.1 of our Signal Protocol Group V2 integration for Percolator. This chunk handles the complex cryptography and orchestration required to add new members or enforce forward secrecy when removing members.

Architectural Constraints (CRITICAL):
* **No `GroupMasterKey` Rotation:** GroupMasterKeys are completely static. Rotating them destroys the group's ZK parameters.
* **Sender Key Rotation on Removal:** If a member is removed (or leaves), all remaining peers must instantly destroy their local Signal `SenderKeyRecord` for that group and generate a new one to prevent the kicked member from decrypting future network traffic.

Implementation Requirements

1. The Proposal Models (`Percolator.Chat`)
* Create `AddMemberProposal(Guid NewMemberPublicIdentityId) : IGroupMutationProposal`.
* Create `RemoveMemberProposal(Guid TargetPublicIdentityId) : IGroupMutationProposal`.

2. The Coordinator Handlers (`GroupMutationCoordinator`)
* Expand `CoordinateMutationAsync` to process Add/Remove proposals.
* **Add Member Flow:**
    * When an `AddMemberProposal` is detected, call `_relayGroupNetworkClient.ModifyGroupAsync` passing the new member's UUID in the `addPublicIdentityIds` array.
    * Once `ModifyGroupAsync` succeeds, construct the `GroupUpdatePayload` (setting `added_public_identity_ids`) and fan it out via `PublishGroupMessageAsync`.
    * **1:1 Bootstrapping:** Dispatch a 1:1 `GroupInvite` containing the `GroupMasterKey` to the new member using the outbox infrastructure (reusing patterns from Chunk 5).
* **Remove Member Flow:**
    * When a `RemoveMemberProposal` is detected, call `_relayGroupNetworkClient.ModifyGroupAsync` passing the target's UUID in the `removePublicIdentityIds` array.
    * The Relay will drop the member from `RelayGroupRosters`, instantly severing their ability to receive messages.
    * **Forward Secrecy Enforced:** Clear out the client's current `SenderKeyRecord` for this group (e.g., call `ISenderKeyCryptographyService.RotateSenderKey(...)`).
    * Generate a new `SenderKeyDistributionMessage`.
    * Enqueue 1:1 `GroupInvite` / `SenderKeyDistribution` envelopes via the outbox to all *remaining* members to share your newly rotated Sender Key.
    * Construct the `GroupUpdatePayload` (setting `removed_public_identity_ids`) and fan it out via `PublishGroupMessageAsync`.

3. Client Ingress Upgrades (`GroupStreamIngressProcessor`)
* Update `GroupStreamIngressProcessor` to detect if a decrypted `GroupContent` contains a `GroupUpdatePayload`.
* If it contains `removed_public_identity_ids`:
    * Verify the author is an Admin.
    * Execute the local DB removal.
    * **Forward Secrecy Enforced:** Just like the author, the receiving client must *immediately* delete their own `SenderKeyRecord` for this group, generate a new one, and enqueue outbox 1:1 envelopes to the remaining peers.
* If it contains `new_group_name`, `added_public_identity_ids`, or `role_updates`, apply them to the local `GroupConversation` and `GroupMembers` SQLite tables.

**Testing Requirements (Chunk 7.1):**
- `GroupMutationCoordinator_CoordinateMutation_AddMember_Sends1to1BootstrappingInvite` - Test that adding a member enqueues a 1:1 invite to the new member alongside the relay mutations.
- `GroupMutationCoordinator_CoordinateMutation_RemoveMember_TriggersSenderKeyRotation` - Test that removing a member clears the local SenderKeyRecord and enqueues distribution messages to remaining peers.
- `GroupStreamIngressProcessor_ProcessGroupMessageAsync_TriggersSenderKeyRotation_OnRemoveMemberPayload` - Test that receiving a legitimate removal payload from an admin causes the client to rotate its own sender key.

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