
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
8. **Remove Dead Code:** Do not simply deprecate unused code. Fully delete code, methods, and classes that are no longer used or have been obsoleted by architectural changes.
9. **Time Representation Rule:** 
   - Domain and Application layers must strictly use `DateTimeOffset`.
   - DBOs and SQLite schemas must store these as Unix-Time-Milliseconds counts (`long` or `INTEGER` in SQLite).
   - Use EF Core's `HasConversion` (`v => v.ToUnixTimeMilliseconds()`, `v => DateTimeOffset.FromUnixTimeMilliseconds(v)`) in `OnModelCreating` so the DBO C# class can still use `DateTimeOffset` cleanly.
   - All columns and properties representing time must be suffixed with `Utc` (e.g., `NextAttemptUtc`, `CreatedAtUtc`) to make their interpretation clear.

---

## Group Messaging Implementation Plan (V2)

**Design & Requirements:**
The goal of this plan is to implement group chat for Percolator. Key requirements include:
* **Optional Relay Role:** The main window can optionally choose to become a relay.
* **Group Hosting:** The main window can use a given relay (including itself) to host a new group chat.
* **Group Membership: The main window can receive and optionally accept/reject group invitations. The main window can send/receive group chat messages once joined.
* **Simulator Support:** The simulator must be updated to serve as both a peer and a relay in group chats.
* **Unauthenticated RPCs:** Some RPC methods must be unauthenticated by design, such as posting a message to a group chat using an HMAC Delivery Ticket (and later, initial x3dh handshake messages which are outside the scope of this plan).
* **Authenticated RPCs:** Other RPC methods are authenticated, where every message has an associated peerId that the endpoint can look up based on provided information (the peerId is not sent in the message itself).
* **Relay Outbox:** Relays require an outbox to fan-out messages to group members.
* **Signal Protocol Logic:** All group logic strictly follows the Signal Protocol Group V2 flows as specified in `session-flow.md`.

## Chunk 1
### Feature Implementation Request: Signal Protocol Chunk 1 (Domain Aggregates & Value Types)
You are to implement all new and updated Domain Aggregates and DDD Value Types required for the Signal Protocol Group V2 architecture.

Implementation Requirements

1. **Strict Value Types & Primitives Avoidance**
* Domains must define their own Value Types to represent concepts. Primitives (int, byte[], Guid) are only allowed inside the Value Type wrappers, DBOs, and Protobufs.
* Use `[ByteArray(minLength, maxLength)]` when creating DDD value types that wrap `byte[]`.
* **Chat Domain Value Types (`Percolator.Chat.ValueObjects`)**:
  * `ConversationId` (Guid wrapper)
  * `ChatRelayId` (Guid/uint wrapper for the Relay's identity in the Chat context)
  * `GroupEpoch` (uint wrapper)
  * `GroupName` (string wrapper)
  * `GroupAvatarId` (ByteArray wrapper for the avatar identifier)
  * `GroupRole` (Enum: Standard = 0, Admin = 1)
  * `[ByteArray(1, 5000)] public sealed partial record EncryptedGroupProfileBytes;`
* **Network Domain Value Types (`Percolator.Network.ValueObjects`)**:
  * `RelayGroupId` (Guid wrapper)
  * `RelayGroupEpoch` (uint wrapper)
  * `NetworkPeerId` (Guid/uint wrapper)
  * `EgressJobId` (Guid wrapper)
  * `DeliveryAttemptCount` (int wrapper)
  * `[ByteArray(1, 5000)] public sealed partial record RelayProfileBytes;`
  * `[ByteArray(1, 5000)] public sealed partial record RelayGroupPublicParamsBytes;`
  * `[ByteArray(1, 1000)] public sealed partial record ZkPresentationBytes;`
  * `[ByteArray(1, int.MaxValue)] public sealed partial record NetworkPayloadBytes;`

2. **Domain Aggregate: `RelayGroupLedger` (`Percolator.Network/RelayLedger`)**
* **Shared Nothing:** Moved from `Percolator.Chat` to `Percolator.Network` to enforce strict DDD bounded contexts. It must only use `Percolator.Network.ValueObjects` (e.g., `RelayGroupId` instead of `ConversationId`).
* **Properties:** `RelayGroupId Id`, `RelayGroupEpoch CurrentEpoch`, `RelayGroupPublicParamsBytes PublicParams`, `RelayProfileBytes EncryptedProfile`, `int ConcurrencyVersion`.
* **Behaviors (Protecting Invariants):**
  * `void ApplyMutation(RelayGroupEpoch baseEpoch, RelayProfileBytes newProfile)`
    Must throw `InvalidOperationException` if `baseEpoch != CurrentEpoch`. Otherwise, increment `CurrentEpoch` and update `EncryptedProfile`.
  * **Rule for Avoiding Exception Control Flow:** Domain Aggregates must throw exceptions to prevent silent corruption of invalid states. To avoid branching based on exceptions, Application layers must proactively verify preconditions before mutating. For example, the Application Service must check `if (ledger.CurrentEpoch != request.BaseEpoch)` *before* calling `ApplyMutation`.

3. **Domain Aggregates: Network Egress Jobs (`Percolator.Network/Egress`)**
* We must not use a single domain aggregate for all network egress.
* Create `AnonymousRelayEgressJob` aggregate root.
    * Properties: `EgressJobId JobId`, `NetworkPeerId RelayPeerId`, `NetworkPayloadBytes PayloadBytes`, `DeliveryAttemptCount Attempts`, `DateTimeOffset NextAttemptUtc`.
    * Behaviors: `void RecordFailure(DateTimeOffset now)` (increments attempts, updates `NextAttemptUtc`), `void MarkSent()`.
* Create `AuthenticatedPeerEgressJob` aggregate root.
    * Properties: `EgressJobId JobId`, `NetworkPeerId DestinationPeerId`, `RoutePreference RoutePreference`, `NetworkPayloadBytes PayloadBytes`, `DeliveryAttemptCount Attempts`, `DateTimeOffset NextAttemptUtc`.
    * Behaviors: `void RecordFailure(DateTimeOffset now)`, `void MarkSent()`.

4. **Domain Aggregate: `GroupConversation` (`Percolator.Chat`)**
* **Shared Nothing:** Only uses `Percolator.Chat.ValueObjects`.
* **State Properties:** `ConversationId Id`, `GroupName Name`, `GroupMasterKey MasterKey`, `GroupEpoch CurrentEpoch`, `GroupAvatarId AvatarId`, and an encapsulated `IReadOnlyCollection<GroupMember> Members` (where `GroupMember` tracks `ChatPeerId` and `GroupRole`).
* **Mutation Proposals:** Define `IGroupMutationProposal` locally. Create:
  * `RenameGroupProposal(GroupName NewName)`
  * `UpdateAvatarProposal(GroupAvatarId NewAvatarId)`
  * `AddMemberProposal(ChatPeerId NewMemberId)`
  * `RemoveMemberProposal(ChatPeerId TargetId)` (Admin kicking someone)
  * `LeaveGroupProposal()` (Member voluntarily leaving)
  * `ChangeMemberRoleProposal(ChatPeerId TargetId, GroupRole NewRole)`
* **Behaviors (Protecting Invariants):**
  * `static GroupConversation CreateNew(ConversationId id, GroupName name, ChatPeerId creatorId, GroupMasterKey key)`
    Enforces Day-Zero invariants (e.g., Epoch = 1, creator is assigned Admin).
  * `GroupMutationFailureReason? ValidateProposal(ChatPeerId actorId, IGroupMutationProposal proposal)` (where `GroupMutationFailureReason` is a domain enum/record).
    Evaluates invariants (e.g., actor is Admin for structural changes like `RemoveMemberProposal` or `RenameGroupProposal`, or actor == target for `LeaveGroupProposal`).
  * `void ApplyProposal(ChatPeerId actorId, IGroupMutationProposal proposal)`
    Must throw `DomainException` if `ValidateProposal` returns a failure. Otherwise, applies the change and correctly increments the `GroupEpoch`.
  * **Rule for Avoiding Exception Control Flow:** The Application Service avoids exception branching by calling `ValidateProposal` first. If it returns a failure (e.g., attempting to remove the last admin), the Application Service gracefully aborts. If it succeeds, it calls `ApplyProposal`, knowing the aggregate will not throw.
* **No Infra Leakage:** Do NOT add Protobuf generation methods to the domain. The Application layer will map domain properties to Protobufs.
* **Domain Events:** Delete `GroupProvisioningRequestedDomainEvent` and `MemberInvitedDomainEvent` (obsolete).

---
## Chunk 2 (Infrastructure Definitions)
### Feature Implementation Request: Signal Protocol Chunk 2 (Persistence & Repositories)
You are to implement all physical database definitions, repositories, and query interfaces required by the new Signal Protocol Group V2 architecture. These definitions must be complete, technically accurate, and ready for code generation. Do not create any domain logic here; this is purely mapping physical storage.

**1. EF Core Table Updates (Relay Blinded Routing & DbContext)**
*   **Target:** `PercolatorDbContext` (in `Percolator.Infrastructure/Persistence`)
    *   **Action:** Remove `public DbSet<NetworkEgressJobDbo> NetworkEgressJobs`.
    *   **Action:** Add `public DbSet<AnonymousRelayEgressJobDbo> AnonymousRelayEgressJobs { get; set; }`.
    *   **Action:** Add `public DbSet<AuthenticatedPeerEgressJobDbo> AuthenticatedPeerEgressJobs { get; set; }`.
    *   **Action:** Rename the existing `RelayOutbox` DbSet to `DomainEventOutbox`.
    *   **Action:** Ensure `RelayGroupStates` and `RelayBlindedRosters` are properly mapped and their schemas updated in `OnModelCreating`.
    *   **Action:** In `OnModelCreating`, configure `HasConversion` on all `DateTimeOffset` properties across *all* Chat and Network DBOs (e.g., `NextAttemptUtc`, `CreatedAtUtc`, `JoinedAtUtc`, `RemovedAtUtc`) to explicitly store them as `long` Unix-Time-Milliseconds in SQLite, satisfying Rule #9.
    *   **Action:** In `OnModelCreating`, configure a strict 1:1 required relationship between `GroupStateDbo` and `GroupCryptoStateDbo` using `.HasOne().WithOne().HasForeignKey()`. This allows seamless hydration of the `GroupConversation` aggregate's Master Key.

*   **Target:** `GroupStateDbo` (in `Percolator.Infrastructure/Chat`)
    *   **Action:** Add `public byte[]? AvatarId { get; set; }` to support avatar updates.
    *   **Action:** Add `public string? Description { get; set; }` for extensibility.

*   **Target:** `RelayBlindedRosterDbo` (Move to `Percolator.Infrastructure/Network/RelayLedger`)
    *   **Action:** Remove `MemberPublicIdentityId`.
    *   **Action:** Add `public byte[] RoutingToken { get; set; } = Array.Empty<byte>();`
    *   **Constraint:** Do **NOT** add any Foreign Key relationships to identity tables. The Relay must remain completely blinded to the true identity of the `RoutingToken`.

*   **Target:** `RelayGroupStateDbo` (Move to `Percolator.Infrastructure/Network/RelayLedger`)
    *   **Action:** Add `public byte[] EncryptedProfile { get; set; } = Array.Empty<byte>();`

**2. Network Egress Persistence (`Percolator.Infrastructure/Network/Egress`)**
*   **Target:** `AnonymousRelayEgressJobDbo`
    *   **Properties:** `Guid JobId`, `byte[] RelayPeerId` (NetworkPeerId), `byte[] PayloadBytes` (NetworkPayloadBytes), `int AttemptCount`, `DateTimeOffset NextAttemptUtc`.
*   **Target:** `AuthenticatedPeerEgressJobDbo`
    *   **Properties:** `Guid JobId`, `byte[] DestinationPeerId` (NetworkPeerId), `int RoutePreference` (enum), `byte[] PayloadBytes` (NetworkPayloadBytes), `int AttemptCount`, `DateTimeOffset NextAttemptUtc`.
*   **Target:** `DomainEventOutboxDbo`
    *   **Action:** Rename the existing `RelayOutboxDbo` to `DomainEventOutboxDbo` to clarify its exact purpose (processing `IDomainEvent` triggers, NOT network bytes). 
    *   **Action:** Update `OutboxDispatcherWorker` to only read from `DomainEventOutboxDbo`.

**3. Repository Interfaces & Implementations**
*   **Target:** `IGroupConversationRepository` (`Percolator.Chat`)
    *   **Action:** Define standard hydration and persistence for the `GroupConversation` aggregate.
    *   **Constraint (Soft Deletes):** When an aggregate removes a member (e.g., `LeaveGroupProposal` or `RemoveMemberProposal`), the repository must *soft-delete* the `GroupMemberDbo` by setting `RemovedAtUtc = DateTimeOffset.UtcNow` rather than physically deleting the row. This preserves UI history.
*   **Target:** `SqliteGroupConversationRepository` (`Percolator.Infrastructure/Chat`)
    *   **Action:** Implement mapping from `GroupStateDbo`, `GroupCryptoStateDbo` (via 1:1 include), and `GroupMemberDbo` into the rich `GroupConversation` aggregate defined in Chunk 1.
*   **Target:** `IAnonymousRelayEgressJobRepository` (`Percolator.Network`)
    *   **Action:** Define standard CRUD for `AnonymousRelayEgressJob`.
*   **Target:** `IAuthenticatedPeerEgressJobRepository` (`Percolator.Network`)
    *   **Action:** Define standard CRUD for `AuthenticatedPeerEgressJob`.
*   **Target:** `SqliteAnonymousRelayEgressJobRepository` & `SqliteAuthenticatedPeerEgressJobRepository` (`Percolator.Infrastructure/Network`)
    *   **Action:** Implement mapping to the new DBOs using isolated EF transactions.
*   **Target:** `IRelayGroupLedgerRepository` (`Percolator.Network/RelayLedger`)
    *   **Action:** Move this interface to `Percolator.Network`.
    *   **Action:** Add `RelayProfileBytes encryptedProfile` to the signature of `ProvisionNewGroupAsync` and ensure it accepts `IReadOnlyList<byte[]> routingTokens` instead of `PublicIdentityId` or `PeerId`.
    *   **Action:** Add method: `Task UpdateGroupStateAsync(RelayGroupLedger ledger, IReadOnlyList<byte[]> addRoutingTokens, IReadOnlyList<byte[]> removeRoutingTokens, CancellationToken cancellationToken);`. This executes the ledger update and the blinded roster insertions/deletions inside a single EF Core transaction.
    *   **Action:** Update `IsMemberAsync` to check if a `RoutingToken` exists in the `RelayBlindedRosterDbo`.

**4. Queries**
*   **Target:** `SqliteRelayRosterQueries` (`Percolator.Infrastructure/Network`)
    *   **Action:** Refactor `GetMemberPeerIdsAsync` to `GetRoutingTokensAsync`. Since `RelayBlindedRosterDbo` now holds `RoutingToken`, simply return the exact bytes. The Relay uses these opaque tokens to route fan-out messages without knowing the true identities.
---

## Chunk 3 (Protobuf Contracts)
### Feature Implementation Request: Signal Protocol Chunk 3 (Network Definitions)
You are to implement all `.proto` contract updates required for the new Relay architecture. These changes establish the exact wire formats and gRPC service signatures without requiring any application-level business logic.

**1. Service Refactoring (`messaging.proto` & `internal_messaging.proto`)**
*   **Action:** Delete `FetchQueuedMessagesRequest`, `FetchQueuedMessagesResponse`, and `RelayOpaqueEnvelope` from `internal_messaging.proto`.
*   **Action:** Delete the obsolete `rpc FetchQueuedMessages` from `InternalMessagingService` in `internal_messaging.proto`.
*   **Action:** Rename the existing `RelayGroupService` to `RelayService` in `messaging.proto`. This service will now handle *all* Relay operations (both 1:1 and Group) to create a unified transport boundary.
*   **Action:** Remove the obsolete `rpc StreamGroupMessages` and `rpc Publish` from `RelayService`.

**2. Group Egress & Relay Operations (`messaging.proto`)**
*   **Action:** Delete `SubmitGroupMessageRequest` and `SubmitGroupMessageResponse`.
*   **Action:** Add the following definitions:
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

    message ProcessAnonymousGroupResponse { 
        optional bool success = 1; 
    }
    ```
*   **Action:** Ensure `AnonymousGroupRequest` handles `FanoutMessagePayload`, `update_encrypted_profile`, and `ModifyMembershipPayload` as an opaque `oneof group_operation`.
*   **Action:** Ensure `ModifyMembershipPayload` uses `repeated bytes routing_tokens = 2;` as defined in `session-flow.md`.
*   **Action:** Update `ProvisionGroupRequest` to include `bytes encrypted_profile = 4;`.
*   **Action:** Add `rpc GetGroupState(GetGroupStateRequest) returns (GetGroupStateResponse);` to `RelayService`.
*   **Action:** Add `rpc ProcessAnonymousGroupRequest(AnonymousGroupRequest) returns (ProcessAnonymousGroupResponse);` to `RelayService`.

**3. Ingress / Stream Operations (`messaging.proto`)**
*   **Action:** Define the new Bidirectional Stream contract. It does **not** include outbound messages.
    ```protobuf
    rpc ConnectRelay(stream ClientRelayStream) returns (stream ServerRelayStream);

    message ClientRelayStream {
        oneof payload {
            MessageAck message_ack = 1;  // Acknowledges an ingress 1:1 message
        }
    }

    message MessageAck {
        optional bytes ack_id = 1; // Guid bytes
    }

    message ServerRelayStream {
        oneof payload {
            GroupMessageDelivery group_delivery = 1;
            OpaqueMessageDelivery opaque_delivery = 2; // For 1:1 queued messages
        }
    }

    message GroupMessageDelivery {
        optional bytes conversation_id = 1;
        optional uint32 epoch = 2;
        optional bytes ciphertext = 3;
        optional bytes sender_presentation = 4;
    }

    message OpaqueMessageDelivery {
        optional bytes ack_id = 1; // Guid bytes
        optional bytes opaque_payload = 2;
    }
    ```

**4. 1:1 Opaque Egress (`messaging.proto`)**
*   **Action:** Update `EnqueueOpaqueMessageRequest` to ensure it represents the new unified opaque drop-off, replacing `target_public_identity_id` with `destination_routing_token` to maintain blind routing:
    ```protobuf
    rpc EnqueueOpaqueMessage(EnqueueOpaqueMessageRequest) returns (EnqueueOpaqueMessageResponse);

    message EnqueueOpaqueMessageRequest {
        optional bytes destination_routing_token = 1; // Matches the blinded roster
        optional uint32 route_preference = 2;
        optional bytes ciphertext = 3;
        optional bytes sender_presentation = 4;
    }

    message EnqueueOpaqueMessageResponse {
        optional bool success = 1;
    }
    ```
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

1. Protobuf Updates (`Percolator.Contracts/Protos`)
* Implement the `.proto` message and service definitions from Chunk 3.

2. Group Encryption Profiles (`Percolator.Cryptography`)
* In `IGroupCryptographyService` (and its concrete implementations), add `EncryptedGroupProfileBytes EncryptGroupProfile(GroupMasterKey masterKey, ReadOnlySpan<byte> profilePlaintext);` and `byte[] DecryptGroupProfile(GroupMasterKey masterKey, EncryptedGroupProfileBytes ciphertext);`. (Use AEAD AES-GCM with a key derived from the master key).

5. Service Implementation (`Percolator.Application/Chat` & `Percolator.Infrastructure/Network`)
* **RelayGroupOperationStatus Enum:** Define an enum `RelayGroupOperationStatus { Success, EpochConflict, Unauthorized, GroupNotFound }` in `Percolator.Application.Chat` to communicate expected domain failures without throwing exceptions.
* **RelayGroupService (gRPC):**
    * **Fail-Fast Structural Validation:** Immediately upon receiving `AnonymousGroupRequest` or `GetGroupStateRequest`, perform strict byte-array length and null checks.
    * **Early Cryptographic Rejection:** Do not pass unauthenticated payloads deep into the Application layer. The gRPC handler must:
      1. Perform a fast, read-only query to fetch the group's `RelayGroupPublicParamsBytes`.
      2. Directly call the Cryptography service to verify the ZK Presentation Proof against the `group_operation` hash.
      3. If verification fails, immediately throw `RpcException(StatusCode.Unauthenticated)`.
    * Update `ProcessAnonymousGroupRequest` to call `RelayGroupOrchestrator.ProcessAnonymousGroupRequestAsync` (which now assumes the payload is pre-authenticated) and evaluate the returned `RelayGroupOperationStatus`, throwing `RpcException(StatusCode.Aborted)` for epoch conflicts.
    * Update `ProvisionGroup` to extract and pass the `EncryptedGroupProfileBytes`.
    * Implement the new `GetGroupState` endpoint. Use early cryptographic rejection as well, then return the epoch, public params, and encrypted profile.

* **RelayGroupOrchestrator:**
    * Replace `PublishGroupRelayMessageAsync` and `ModifyGroupAsync` concepts with a unified `Task<RelayGroupOperationStatus> ProcessAnonymousGroupRequestAsync(AnonymousGroupRequest request, CancellationToken ct);`.
    * In `ProcessAnonymousGroupRequestAsync`: Since the ZK proof is validated at the gRPC boundary, simply route the unmarshalled struct to internal private methods: `FanoutAsync`, `UpdateProfileAsync`, or `ModifyMembershipAsync`.
    * `FanoutAsync`: Queue the message via `RelayOutboxDbo` using `TargetRoutingToken`.
    * `UpdateProfileAsync`: Check `if (ledger.CurrentEpoch != baseEpoch) return RelayGroupOperationStatus.EpochConflict;` then call `ledger.ApplyMutation(baseEpoch, newProfile);` and update `RelayGroupStates`.
    * `ModifyMembershipAsync`: Call `IRelayGroupLedgerRepository.UpdateGroupStateAsync` to persist the ledger changes alongside the insertions/deletions of `RoutingToken`s in the `RelayBlindedRosters` table.

**Testing Requirements (Chunk 6.1):**
- `RelayGroupOrchestrator_ProcessAnonymousGroupRequestAsync_FanoutDoesNotAdvanceEpoch` - Ensure that chat messages only verify auth and fan out, leaving the epoch unchanged.
- `RelayGroupOrchestrator_ProcessAnonymousGroupRequestAsync_ReturnsEpochConflict_WhenEpochMismatched` - Test that providing an incorrect epoch returns the new `EpochConflict` enum instead of throwing an exception.
- `RelayGroupOrchestrator_ProcessAnonymousGroupRequestAsync_ModifyProfileAdvancesEpochAndUpdatesProfile` - Test that the new mutation method properly increments the epoch and stores the new parameters.
- `RelayGroupOrchestrator_ProcessAnonymousGroupRequestAsync_ModifyMembershipUpdatesRoster_MappingToRoutingTokens` - Test that the mutation method correctly uses opaque RoutingTokens for blinded roster updates without leaking identity.

---
## Chunk 6.2
### Feature Implementation Request: Signal Protocol Chunk 6.2 (Bidirectional Stream & Privacy-Preserving Egress)
You are to replace the fragmented, pull-based 1:1 messaging model and the conversation-centric group streaming model with a singular, bidirectional gRPC stream per Relay. This chunk enforces strict "shared nothing" Clean Architecture across the `Network`, `Chat`, `Cryptography`, and `Identity` domains. Critically, it corrects a privacy flaw by strictly segregating authenticated ingress streams from anonymous egress RPCs to preserve the Sealed Sender protocol.

#### Architectural Overview & Problem Analysis
**AI AGENT DIRECTIVE:** Intermediate compilation is NOT required between Sub-Chunks A through D. You will intentionally break the build by changing contracts and splitting interfaces. **Do NOT attempt to fix cascading compilation errors outside the scope of your current Sub-Chunk.** All legacy wiring will be cleaned up in Sub-Chunk E.

* **Cross-Domain Value Types Violation:** 
  * Ensure `AnonymousRelayEgressJob` and `AuthenticatedPeerEgressJob` use `Percolator.Network.NetworkPeerId` for routing.
  * Ensure `RelayHostStreamManager` uses `Percolator.Identity.PublicIdentityId` for stream tracking. 
  * Do NOT leak `Percolator.Chat.GroupLedger.PublicIdentityId` into the Infrastructure network tracking.
* **The Sealed Sender Privacy Boundary (CRITICAL):** Signal's Sealed Sender protocol dictates that the Relay knows who is receiving a group message, but *not* who sent it.
  * **Solution:** **Ingress is Authenticated, Egress has Split Authorization.** We will use a Bidirectional gRPC Stream *exclusively* for retrieving messages and sending Acks. Egress remains **Unary RPCs**. 
  * Group Egress (`ProcessAnonymousGroupRequest`) is dispatched anonymously (omitting `DeliveryCertificate` headers) relying entirely on ZK Proofs for authorization. 
  * 1:1 Egress (`EnqueueOpaqueMessage`) *must* attach the sender's `DeliveryCertificate` headers to prevent mailbox-spam DoS on the relay, as we do not yet have recipient-issued Sealed Sender tokens for 1:1s.
* **Lack of Egress Outbox:** Currently, we lack a dedicated persistent Egress queue for network routing payloads. We use direct ephemeral RPCs or internal envelopes. Refer to Chunk 2 for new definitions.
* **Greenfield Cutover:** As this is greenfield development, we do not write EF migrations. We define the new code, update the EF DbContext to drop the old tables/create the new ones, and delete the legacy code once cutover is complete.

---

### Sub-Chunk A: Protobuf Contracts & Network Domain Egress
**Goal:** Define the bidirectional gRPC contracts (strictly for ingress/acks) and establish the `Network` domain aggregate for reliable outbound routing.

1. **Protobuf Updates (`Percolator.Contracts`)**
* Implement the `.proto` message and service definitions from Chunk 3.

1. **Network Domain (`Percolator.Network`)**
* Implement definitions from Chunk 2.
* **Tests (`Percolator.NetworkTests`)**:
  * `AnonymousRelayEgressJob_RecordFailure_IncrementsAttemptAndSetsNextAttemptUtc` (Adhere strictly to deterministic time testing using hard-coded `DateTimeOffset` values).
  * `AuthenticatedPeerEgressJob_MarkSent_UpdatesStateToSent`.

---

### Sub-Chunk B: Infrastructure Egress Persistence & Worker
**Goal:** Implement the physical storage and the background worker that drains the egress queue anonymously.

1. **Infrastructure Persistence (`Percolator.Infrastructure`)**
* Review Chunk 2 and map DBOs to the EF Core context.

2. **Application & Infrastructure Orchestration**
* Create `NetworkEgressWorker` (BackgroundService) in `Percolator.Infrastructure/Egress`.
* Logic: `ExecuteAsync` runs a continuous loop (with a 5-second `Task.Delay` polling interval). It polls both `IAnonymousRelayEgressJobRepository` and `IAuthenticatedPeerEgressJobRepository` for jobs where `NextAttemptUtc <= Now`.
    * Deserialize the job and dispatch it using standard **Unary gRPC Clients**.
    * **Direct (P2P) Routing (Authenticated):** If `RoutePreference` is Direct, use `IPeerGrpcChannelFactory` to get a client for the destination `NetworkPeerId` and call the existing `PercolatorMessageService.DeliverOpaqueMessage`.
    * **Relay Routing (Authenticated):** Call `RelayService.EnqueueOpaqueMessage(EnqueueOpaqueMessageRequest)`, and *do* attach the headers (authenticated drop-off).
    * **Relay Routing (Anonymous):** Call the unary `RelayService.ProcessAnonymousGroupRequest(AnonymousGroupRequest)` endpoint, and do *not* attach the sender's `DeliveryCertificate` identity headers (anonymous egress).
    * On success, delete the job. On failure, invoke `RecordFailure()` and save.
* **Tests (`Percolator.InfrastructureTests`)**:
  * `NetworkEgressWorker_DispatchesPayload_AndDeletesJobOnSuccess`.
  * `NetworkEgressWorker_RecordFailure_UpdatesAttemptCountAndNextAttemptUtc`.
  * `NetworkEgressWorker_RoutePreferenceDirect_UsesDirectClient`.
  * `NetworkEgressWorker_RoutePreferenceRelay_UsesRelayClientWithAnonymousAuth` (Group).
  * `NetworkEgressWorker_RoutePreferenceRelay_UsesRelayClientWithAuthFor1to1`.

---

### Sub-Chunk C: Peer as Relay Host (Server-Side Ingress Management)
**Goal:** Manage active streaming sockets when this node acts as a Relay Server, enforcing Certificate Authentication for connecting downstream clients.

1. **Stream Authentication (`Percolator.Infrastructure/Network/RelayHost`)**
* Update `DeliveryCertificateAuthInterceptor` to override `DuplexStreamingServerHandler`. 
* This ensures that when a downstream client calls `ConnectRelay`, the gRPC pipeline verifies the `x-percolator-signature`, `x-percolator-timestamp`, and `x-percolator-sender-public-identity-id` headers, securing the ingress socket so the Relay knows which queues to flush to this connection.

2. **Application Logic (`Percolator.Application/Network/RelayHost`)**
* Create `RelayHostStreamManager`. It must be a DI Singleton utilizing a `ConcurrentDictionary<Percolator.Identity.PublicIdentityId, IServerStreamWriter<ServerRelayStream>>` for thread safety.
* **Query/Mutation Split:** Split the existing `IMessageQueueRepository` interface by moving `FetchAsync` into a new `IMessageQueueQueries` interface (`Task<IReadOnlyList<(Guid AckId, QueuedPayloadBytes Blob)>> FetchAsync(PublicIdentityId recipientPublicIdentityId, int maxCount, CancellationToken ct)`). The remaining enqueue/delete methods stay in `IMessageQueueRepository` (mutations). Ensure `SqliteMessageQueueRepository` in the infrastructure layer implements both interfaces.
* When `ConnectRelay` passes the interceptor, the server loops over the `IAsyncStreamReader<ClientRelayStream>`.
    * On connect, query `IMessageQueueQueries` to retrieve any pending messages and push them as `OpaqueMessageDelivery` downwards.
    * On `message_ack`, delete the queued item using `IMessageQueueRepository`.
    * **Stream Lifecycle & Cert Freshness:** The server's `MoveNext` loop must periodically evaluate the client's `DeliveryCertificate.ExpiresAtUtc`. If it expires, throw an `RpcException` to forcefully drop the stream. Wrap the entire connection handler in a `try/finally { _manager.Remove(clientId); }` block to ensure disconnects are immediately purged from the `ConcurrentDictionary`.
* **Tests (`Percolator.ApplicationTests`)**:
  * `RelayHostStreamManager_RegisterClient_FlushesExistingQueue`.
  * `RelayHostStreamManager_StreamDisconnect_RemovesFromDictionary`.
  * `RelayHostStreamManager_CertificateExpired_DropsStreamWithRpcException`.

---

### Sub-Chunk D: Peer as Relay Client (Upstream Connection Worker)
**Goal:** Manage this node's continuous authenticated ingress connection to external Relay Hosts.

1. **Application Logic (`Percolator.Application/Network`)**
* **Extract 1:1 Ingress Routing:** Define `IOpaqueMessageDeliverer` in `Percolator.Application/Network`. 
* Extract the relevant inbound logic from the bloated `DeliverOpaqueMessageHandler` into this new service: `Task<bool> DeliverAsync(byte[] opaqueBytes, CancellationToken ct)`. It must handle Ratchet decryption, `DirectSession` lookup, and `InternalEnvelope` dispatching. If `DeliverAsync` returns true, the caller should yield a `MessageAck`. Do **not** use MediatR for this outer loop routing.
* Update `DeliverOpaqueMessageHandler` to either delegate to this new service or remove the extracted logic entirely.

2. **Infrastructure Logic (`Percolator.Infrastructure/Network/Upstream`)**
* Create `UpstreamRelayStreamWorker` (Replacing `RelayGroupStreamWorker`).
* **Connection Lifecycle & Concurrency:** Add a new method `Task<IEnumerable<PeerRoutingProfile>> GetAllAsync(CancellationToken ct)` to `IPeerRoutingProfileRepository` and implement it in `SqlitePeerRoutingProfileRepository`. The worker then calls `GetAllAsync()` and filters for profiles where `Relays.Count > 0` to resolve the set of all active Relay `NetworkPeerId`s. Launch a background task for each relay using an unbounded `Task.WhenAll` loop (as the expected count is small—tens at most). It must implement an exponential backoff loop for reconnections upon stream failure.
* **Client Authentication & Expiry:** Before invoking `ConnectRelay`, query `IDeliveryCertificateStore.GetCertificateAsync()` to retrieve this node's `DeliveryCertificate`. Generate the cryptographic signature over the current timestamp and append the standard `x-percolator-*` headers to the gRPC `CallOptions`.
    * *Note on Cert Freshness:* If the Relay Host drops the stream because the certificate expired mid-session, the worker's catch block triggers the backoff loop, fetches the newest certificate (which `DeliveryCertificateRefreshWorker` keeps updated in the background), and successfully reconnects.
* **Ingress Pipeline:**
    * Loop over `ServerRelayStream` `oneof`. 
    * Route payloads by directly invoking service methods. For group deliveries, call `IGroupStreamIngressProcessor.ProcessGroupMessageAsync`.
    * For 1:1 deliveries, call the newly created `IOpaqueMessageDeliverer.DeliverAsync()`. If it returns true, immediately push a `MessageAck` back up the `ClientRelayStream`.
* **Tests (`Percolator.ApplicationTests`)**:
  * `IOpaqueMessageDeliverer_DeliverAsync_Success_ReturnsTrue`.
  * `IOpaqueMessageDeliverer_DeliverAsync_DecryptionFailure_ReturnsFalse`.
* **Tests (`Percolator.InfrastructureTests`)**:
  * `UpstreamRelayStreamWorker_YieldsAckUpstream_WhenOpaqueDeliveryReceived`.
  * `UpstreamRelayStreamWorker_ConnectionFailure_ImplementsExponentialBackoff`.
  * `UpstreamRelayStreamWorker_CertificateExpired_RefetchesAndReconnects`.

---

### Sub-Chunk E: Cutover & Dead Code Elimination
**Goal:** Migrate existing usage and delete obsolete code.

1. **Refactoring Handlers (`Percolator.Application`)**
* **AI Action:** Use your code search tools to explicitly find all Application Handlers that inject `ISessionMessageService` or `RouteSender`.
* Update all of these discovered paths to instead construct an `AuthenticatedPeerEgressJob` or `AnonymousRelayEgressJob` and save it to the corresponding repository for routing payloads (e.g., chat message sending). This establishes durability and cuts ties to the legacy ephemeral senders.

2. **Dead Code Deletion**
* **Implementation Files:**
  * Delete `RelayGroupStreamWorker.cs`.
  * Delete `GrpcRelayGroupStreamDispatcher.cs` & `IRelayGroupStreamDispatcher.cs`.
  * Delete `RelayOrchestrator.cs` (The legacy pull-based orchestrator).
  * Delete `FetchQueuedMessagesHandler.cs` and related Pull-model contracts (`FetchQueuedMessagesQuery.cs`, `FetchQueuedMessagesResult.cs`).
  * Delete `TryRelayNextForPeerCommand.cs` (command that uses deleted `RelayOrchestrator`).
  * Delete `ProcessRelayedOpaquePayloadCommand.cs` (references `RelayOpaqueEnvelope` which is being deleted from protobuf).
  * Rename `RelayGroupService.cs` to `RelayService.cs` and update the class to inherit from `RelayService.RelayServiceBase` instead of `RelayGroupService.RelayGroupServiceBase`. Update `GrpcServerManager.cs` to register `RelayService` instead of `RelayGroupService`.
* **Test Files:**
  * Delete `RelayOrchestratorTests.cs`.
  * Delete `FetchQueuedMessagesHandlerTests.cs`.
  * Delete `GrpcRelayGroupStreamDispatcherTests.cs`.
* **Service Registrations:**
  * Remove `services.AddScoped<RelayOrchestrator>();` from `Percolator.Application/Network/ServiceCollectionExtensions.cs`.
  * Remove the `IRelayGroupStreamDispatcher` registration from `Percolator.Infrastructure/Network/ServiceCollectionExtensions.cs`.
* **Dependency Cleanup:**
  * Remove `_relayOrchestrator` dependency from `DeliverOpaqueMessageHandler.cs` and delete the relay loop logic (lines 213-224) that calls `RelayNextAsync`.
* **Database Cleanup:**
  * Ensure the SQLite database drops the legacy `RelayOutbox` tables.

---

## Chunk 7
### Feature Implementation Request: Signal Protocol Chunk 7 (Client-Side Speculative Rebase Coordinator)
You are to implement Chunk 7 of our Signal Protocol Group V2 integration for Percolator, isolating client-side conflict resolution behind a reusable Process Manager.

Architectural Constraints (CRITICAL):
* **No Dirty Memory States:** Do not apply state mutations directly to tracked repository entities before formal network confirmation. Speculative mutations must be verified cleanly without dirtying live cache entities.
* **Reusable Coordination Over Indirection:** Do not write custom retry loops or network synchronization blocks inside individual handlers. Centralize this orchestration within an application-layer Process Manager (`GroupMutationCoordinator`).
* **Intent-Based Validation via CQRS:** Group mutations must be modeled as structural proposals so they can be re-evaluated for validity if the group baseline shifts during a sync catch-up execution loop.
* **Sealed Sender Compatibility:** The Relay is completely opaque. It tracks the roster for fan-out but does NOT know the sender of a group message. All mutations are just opaque ciphertexts published via the existing `ProcessAnonymousGroupRequest` gRPC endpoint, which uses ZK proofs (`presentation`) for authorization without revealing identity.

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

2. The Mutation Coordinator Process Manager (`Percolator.Application/Apps/Chat`)
* Create a centralized service orchestrator: `GroupMutationCoordinator`.
* Define a new network interface in `Percolator.Application/Chat`: `IRelayGroupNetworkClient`. It should expose `ProcessAnonymousGroupRequestAsync` and `GetGroupStateAsync` using strictly domain types (e.g., `ZkPresentationBytes`, `EncryptedGroupProfileBytes`, `CiphertextBytes`), fully abstracting away gRPC and Protobufs.
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
    * **Step 1:** Load a fresh instance of the aggregate from `IGroupConversationRepository` and evaluate it using `CanApplyProposal`.
    * **Step 2:** If it fails validation due to a state change found during catch-up, abort instantly and return `MutationResult.Failed(reason)`.
    * **Step 3 (Relay Ledger Update):** The Application service reads the semantic state from the `group` to manually build the `GroupProfilePlaintext` protobuf. Encrypt this using `IGroupCryptographyService.EncryptGroupProfile` to generate an `EncryptedGroupProfileBytes`. Generate a `ZkPresentationBytes` and call `_relayGroupNetworkClient.ProcessAnonymousGroupRequestAsync(...)` specifying the `update_encrypted_profile` branch of the `AnonymousGroupRequest` operation.
    * **Step 4 (On Conflict):** If `ProcessAnonymousGroupRequestAsync` throws an `EpochConflictException` indicating an epoch conflict or verification failure, call `_relayGroupNetworkClient.GetGroupStateAsync(...)` to fetch the authoritative latest state. Decrypt the returned `EncryptedProfile`, apply it to the local SQLite database to advance the baseline, and retry the loop.
    * **Step 5 (Fan-Out Broadcast):** Once `ProcessAnonymousGroupRequestAsync` succeeds, construct the `GroupUpdatePayload` protobuf (the diff). Encrypt it using `ISenderKeyCryptographyService.EncryptGroupMessage(...)`. Dispatch the frame to the relay using `_relayGroupNetworkClient.ProcessAnonymousGroupRequestAsync(...)` specifying the `fanout_message` branch. 
    * **Step 6 (Commit):** Apply the mutation directly to the domain object, commit it locally using `_repository.UpdateAsync(...)`, and return success.

4. Infrastructure Network Client (`Percolator.Infrastructure/Network`)
* Implement `RelayGroupNetworkClient : IRelayGroupNetworkClient`.
* This class is responsible for injecting the gRPC `RelayServiceClient`, translating domain types into `AnonymousGroupRequest` and `GetGroupStateRequest` Protobufs, executing the RPC calls, and wrapping `RpcException(StatusCode.Aborted)` into `EpochConflictException`.

4. Refactored Application Handlers (`Percolator.Application/Apps/Chat`)
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

1. The Coordinator Handlers (`GroupMutationCoordinator`)
* Expand `CoordinateMutationAsync` to process Add/Remove proposals.
* **Add Member Flow:**
    * When an `AddMemberProposal` is detected, call `_relayGroupNetworkClient.ProcessAnonymousGroupRequestAsync` specifying the `modify_membership` branch and passing the new member's UUID.
    * Once `ProcessAnonymousGroupRequestAsync` succeeds, construct the `GroupUpdatePayload` (setting `added_public_identity_ids`) and fan it out via the `fanout_message` branch.
    * **1:1 Bootstrapping:** Dispatch a 1:1 `GroupInvite` containing the `GroupMasterKey` to the new member using the outbox infrastructure (reusing patterns from Chunk 5).
* **Remove Member Flow:**
    * When a `RemoveMemberProposal` is detected, call `_relayGroupNetworkClient.ProcessAnonymousGroupRequestAsync` specifying the `modify_membership` branch and passing the target's UUID.
    * The Relay will drop the member from `RelayGroupRosters`, instantly severing their ability to receive messages.
    * **Forward Secrecy Enforced:** Clear out the client's current `SenderKeyRecord` for this group (e.g., call `ISenderKeyCryptographyService.RotateSenderKey(...)`).
    * Generate a new `SenderKeyDistributionMessage`.
    * Enqueue 1:1 `GroupInvite` / `SenderKeyDistribution` envelopes via the outbox to all *remaining* members to share your newly rotated Sender Key.
    * Construct the `GroupUpdatePayload` (setting `removed_public_identity_ids`) and fan it out via the `fanout_message` branch.

2. Client Ingress Upgrades (`GroupStreamIngressProcessor`)
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