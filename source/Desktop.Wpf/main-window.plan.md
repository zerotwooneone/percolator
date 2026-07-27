
## Rules of Execution for AI Agents

1. **File Locations & Namespaces:**
   - Persistence (DBOs): `Percolator.Infrastructure/Chat/Persistence`
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
   - PeerId is a uint auto-incrementing identifier that refers to a remote identity
   - PeerId is a local only identifier, it must NEVER be sent over the wire
   - SelfId is a uint auto-incrementing identifier that refers to an identity local to the current application.
   - SelfId is a local only identifier, it must NEVER be sent over the wire
   - PublicIdentityId is a guid which uniquely identifies **either** a selfId or a peerId this may be sent over the wire
7. **No Shims or Temporary Code:** Do not implement shims or temporary code that does not exist in the plan. Do not write methods that throw `new NotImplementedException` - instead stop and ask the user what should be done. Each chunk must implement zero guesses. Do not write todo comments - instead stop and ask the user how to proceed.
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
* **Unauthenticated RPCs:** Some RPC methods must be unauthenticated by design, such as posting a message to a group chat using an Delivery Ticket (and later, initial x3dh handshake messages which are outside the scope of this plan).
* **Authenticated RPCs:** Other RPC methods are authenticated, where every message has an associated peerId that the endpoint can look up based on provided information (the peerId is not sent in the message itself).
* **Relay Outbox:** Relays require an outbox to fan-out messages to group members.
* **Signal Protocol Logic:** All group logic strictly follows the Signal Protocol Group V2 flows as specified in `session-flow.md`.

## Chunk 1
### Feature Implementation Request: Signal Protocol Chunk 1 (Domain Model Overhaul)

**1. Deletions (Clean Slate)**
*   Delete `Percolator.Chat/GroupLedger/RelayGroupPublicParamsBytes.cs` (Obsolete, ZK anchor uses an opaque blob).
*   Delete `Percolator.Chat/GroupLedger/RelayGroupLedger.cs` (Moving to Network domain).
*   Delete `Percolator.Chat/GroupLedger/IRelayGroupLedgerRepository.cs` (Moving to Network domain).
*   Remove any logic in `Percolator.Chat/GroupLedger/GroupConversation.cs` related to `RelayGroupPublicParamsBytes` or `GroupMasterKeyBytes`.
*   Delete any old event or request classes tied to Group V1 if they exist (e.g., `GroupProvisioningRequestedDomainEvent`, `ModifyGroupRequest`).

**2. Network Domain Value Types (`Percolator.Network/ValueObjects`)**
*   Create `EncryptedEntriesBlobBytes.cs`: `[ByteArray(1, 5000)] public sealed partial record EncryptedEntriesBlobBytes;`
*   Ensure `RelayGroupId` (Guid wrapper), `RelayGroupEpoch` (uint wrapper), and `NetworkPeerId` (uint wrapper) exist and are structurally correct.

**3. Chat Domain Value Types (`Percolator.Chat/ValueObjects`)**
*   Create `SenderKeyId.cs`: `public readonly record struct SenderKeyId(uint Value);` (O(1) ratchet lookup).
*   Create `EncryptedGroupProfileBytes.cs`: `[ByteArray(1, 5000)] public sealed partial record EncryptedGroupProfileBytes;`
*   Ensure `ConversationId` (Guid wrapper), `GroupEpoch` (uint wrapper), `GroupName` (string wrapper), `GroupAvatarId` (ByteArray wrapper), `GroupRole` (Enum) exist and are structurally correct.
*   Ensure `ChatPeerId` (uint wrapper) exists to represent remote peers within the Chat domain.

**4. Cryptography Domain Value Types (`Percolator.Cryptography/GroupLedger`)**
*   Create `ZkPresentationBytes.cs`: `[ByteArray(1, 1000)] public sealed partial record ZkPresentationBytes;`
*   Create `AuthCredentialMacBytes.cs`: `[ByteArray(1, 1000)] public sealed partial record AuthCredentialMacBytes;`

**4a. Shared Egress / Outbox Domain (`Percolator.Network/Egress`)**
*   Create `RelayEgressJob` (Aggregate Root): represents a pending payload that the Relay must deliver to a connected client. The payload should be generalized as it will wrap `ServerRelayStream` messages.
    *   Properties: `Guid JobId`, `NetworkPeerId DestinationPeerId`, `byte[] PayloadBytes` (the serialized `ServerRelayStream` protobuf), `int AttemptCount`, `DateTimeOffset NextAttemptUtc`.

**5. Domain Aggregate: `RelayGroupLedger` (`Percolator.Network/RelayLedger`)**
*   Create `RelayGroupLedger.cs` in `Percolator.Network/RelayLedger`.
*   **Properties:** `RelayGroupId Id`, `RelayGroupEpoch CurrentEpoch`, `EncryptedEntriesBlobBytes EncryptedEntriesBlob`, `int ConcurrencyVersion`.
*   **Behaviors:**
    *   `private RelayGroupLedger(...)` constructor.
    *   `public static RelayGroupLedger CreateNew(RelayGroupId id, EncryptedEntriesBlobBytes initialBlob)`
    *   `public void OverwriteState(RelayGroupEpoch baseEpoch, EncryptedEntriesBlobBytes newBlob)`
        Throws `InvalidOperationException` if `baseEpoch != CurrentEpoch`. Otherwise, increments `CurrentEpoch` by 1 and overwrites `EncryptedEntriesBlob`.
*   **Unit Tests (`RelayGroupLedgerTests.cs`):**
    *   Test: `OverwriteState_WhenBaseEpochMatches_UpdatesBlobAndIncrementsEpoch`
    *   Test: `OverwriteState_WhenBaseEpochDiffers_ThrowsInvalidOperationException` (Testing the Epoch Conflict mechanism).

**6. Domain Aggregate: `GroupConversation` Refactoring (`Percolator.Chat/GroupLedger`)**
*   Update `GroupConversation.cs`.
*   **Properties:** `ConversationId Id`, `GroupName Name`, `GroupEpoch CurrentEpoch`, `GroupAvatarId AvatarId`, `IReadOnlyCollection<GroupMember> Members`.
*   *(Note: Cryptographic primitives like `GroupMasterKeyBytes` and `AuthCredentialMacBytes` belong in the `Cryptography` domain or are managed by specialized interfaces. The Chat domain aggregate ONLY models the semantic group state.)*
*   **Removal:** Delete `GroupMasterKey` and `PublicParams` properties from this class entirely. Update `CreateNew` accordingly.
*   **Behaviors (Protecting Invariants):**
    *   `public void RenameGroup(ParticipantId actorParticipantId, GroupName newName)`: Throws `UnauthorizedDomainException` if actor is not Admin. Updates name and increments `GroupEpoch`.
    *   `public void UpdateAvatar(ParticipantId actorParticipantId, GroupAvatarId newAvatarId)`: Throws `UnauthorizedDomainException` if actor is not Admin. Updates avatar and increments `GroupEpoch`.
    *   `public void AddMember(ParticipantId actorParticipantId, ParticipantId newMemberParticipantId)`: Throws `UnauthorizedDomainException` if actor is not Admin or member already exists. Adds member and increments `GroupEpoch`.
    *   `public void RemoveMember(ParticipantId actorParticipantId, ParticipantId targetParticipantId)`: Throws `UnauthorizedDomainException` if actor is not Admin, target not found, or removing last admin. Soft-deletes member and increments `GroupEpoch`.
    *   `public void LeaveGroup(ParticipantId actorParticipantId)`: Throws `UnauthorizedDomainException` if actor is last admin. Soft-deletes actor and increments `GroupEpoch`.
    *   `public void ChangeMemberRole(ParticipantId actorParticipantId, ParticipantId targetParticipantId, GroupMemberRole newRole)`: Throws `UnauthorizedDomainException` if actor is not Admin, target not found, or demoting last admin. Updates role and increments `GroupEpoch`.
*   **Unit Tests (`GroupConversationTests.cs`):**
    *   Test: `RenameGroup_WhenActorIsAdmin_UpdatesNameAndIncrementsEpoch`
    *   Test: `RenameGroup_WhenActorIsNotAdmin_ThrowsUnauthorizedDomainException`
    *   Test: `LeaveGroup_WhenActorIsLastAdmin_ThrowsUnauthorizedDomainException`
    *   Test: `AddMember_WhenMemberAlreadyExists_ThrowsUnauthorizedDomainException`

**7. New Domain Aggregates: Client Security State (`Percolator.Cryptography/GroupLedger`)**
*   *Note: These aggregates reside in the Cryptography domain to preserve "Shared Nothing" clean architecture.*
*   Create `GroupCredentials.cs`:
    *   Properties: `GroupId Id` (Guid-based from Cryptography domain), `GroupMasterKey MasterKey`, `AuthCredentialMacBytes AuthCredentialMac`.
*   Create `SenderKeyRatchet.cs`:
    *   Properties: `GroupId Id`, `CryptoPublicIdentityId AuthorPublicIdentityId`, `uint KeyId`, `ChainKey ChainKey`, `SignaturePublicKey SignatureKey`.
*   Create `UnknownMessageCache.cs`:
    *   Properties: `long Id`, `GroupId GroupId`, `uint MissingKeyId`, `Ciphertext Ciphertext`, `DateTimeOffset ReceivedAtUtc`.
*   Create `SkippedMessageKey.cs`:
    *   Properties: `GroupId GroupId`, `uint KeyId`, `int MessageIndex`, `byte[] MessageKey`.
*   **Unit Tests:**
    *   Ensure any complex logic inside these (like FIFO eviction on the cache) gets Black Box unit tests. e.g. `UnknownMessageCache_WhenCapacityExceeded_EvictsOldestAndGeneratesTombstone`.

---
## Chunk 2 (Infrastructure Definitions)
### Feature Implementation Request: Signal Protocol Chunk 2 (Persistence & Repositories)
You are to completely remove the old SQL/EF definitions and implement all new physical database definitions, repositories, and query interfaces required by the Signal Protocol Group V2 architecture. Do not create any domain logic here; this is purely mapping physical storage.

**1. Deletions (Clean Slate Infrastructure)**
*   Delete `GroupCryptoStateDbo.cs`.
*   Delete `RelayGroupStateDbo.cs`.
*   Delete `RelayBlindedRosterDbo.cs` (The Relay no longer stores network topologies or routing rosters).
*   Delete `IRelayRosterQueries.cs` and `SqliteRelayRosterQueries.cs`.
*   Remove `RelayBlindedRosters` and `GroupCryptoStates` DbSets and their configurations from `PercolatorDbContext`.

**2. Relay Server Persistence (`Percolator.Infrastructure/Network/RelayLedger`)**
*   Create `RelayGroupLedgerDbo.cs`:
    *   Properties: `Guid ConversationId` (PK), `uint Epoch`, `byte[] EncryptedEntriesBlob`.
*   Update `IRelayGroupLedgerRepository` (in `Percolator.Network`) and `SqliteRelayGroupLedgerRepository`:
    *   Map `RelayGroupLedger` aggregate to `RelayGroupLedgerDbo`.
    *   Methods: `GetByIdAsync`, `OverwriteStateAsync`.

**2a. Egress Outbox Persistence (`Percolator.Infrastructure/Network/Egress`)**
*   Create `RelayEgressJobDbo.cs` for storing generalized payloads waiting for peer connection. Initially, this will primarily be used for transient fan-out group messages wrapped in the `ServerRelayStream` envelope, but it will eventually accommodate other types of outbox messages (like 1:1 messages).
    *   Properties: `Guid JobId` (PK), `uint DestinationPeerId`, `byte[] PayloadBytes`, `int AttemptCount`, `long NextAttemptUtc`.
*   Ensure mapping exists in `PercolatorDbContext`.

**3. Client Persistence - Cryptography Domain (`Percolator.Infrastructure/Cryptography/GroupLedger`)**
*   Create `GroupCredentialsDbo.cs`:
    *   Properties: `Guid ConversationId` (PK), `byte[] GroupMasterKey`, `byte[] AuthCredentialMac`.
*   Create `SenderKeyRatchetDbo.cs`:
    *   Properties: `Guid ConversationId` (PK Part 1), `uint SenderKeyId` (PK Part 2), `byte[] AuthorPublicIdentityId`, `byte[] ChainKey`, `byte[] SignatureKey`.
*   Create `UnknownMessageCacheDbo.cs`:
    *   Properties: `long Id` (PK Auto), `Guid ConversationId`, `uint MissingKeyId`, `byte[] Ciphertext`, `long ReceivedAtUtc`.
*   Create `SkippedMessageKeyDbo.cs`:
    *   Properties: `Guid ConversationId` (PK Part 1), `uint SenderKeyId` (PK Part 2), `int MessageIndex` (PK Part 3), `byte[] MessageKey`.

**4. Client Persistence - Chat Domain (`Percolator.Infrastructure/Chat/Persistence`)**
*   Update `GroupStateDbo.cs`:
    *   Keep: `Guid ConversationId` (PK), `uint Epoch`, `string? Name`, `byte[]? AvatarId`.
    *   Remove: `byte[] PublicParams` (gone in ZK model), `uint RelayPeerId` (routing is transient now).
*   Update `GroupMemberDbo.cs`:
    *   Add: `byte[] ProfileKey`.
    *   Ensure exact mapping: `Guid ConversationId`, `byte[] PublicIdentityId`, `int Role`.

**5. DbContext Configuration (`Percolator.Infrastructure/Persistence/PercolatorDbContext.cs`)**
*   Add `DbSet`s for `RelayGroupLedgers`, `GroupCredentials`, `SenderKeyRatchets`, `UnknownMessageCaches`, `SkippedMessageKeys`, and `RelayEgressJobs`.
*   In `OnModelCreating`, configure the composite keys for `SenderKeyRatchetDbo` and `SkippedMessageKeyDbo`.
*   Configure `HasConversion` for time properties (e.g., `UnknownMessageCacheDbo.ReceivedAtUtc`, `RelayEgressJobDbo.NextAttemptUtc`) to explicit `long` (Unix Time Milliseconds) per Rule #9.

**6. Repository Interfaces & Implementations**
*   Create `IGroupCredentialsRepository` / `SqliteGroupCredentialsRepository`.
*   Create `ISenderKeyRatchetRepository` / `SqliteSenderKeyRatchetRepository`.
*   Create `IUnknownMessageCacheRepository` / `SqliteUnknownMessageCacheRepository`.
*   Create `IRelayEgressJobRepository` / `SqliteRelayEgressJobRepository`.
*   Update `IGroupConversationRepository` / `SqliteGroupConversationRepository` to map only semantic state (no crypto). When an aggregate removes a member, the repository must soft-delete the `GroupMemberDbo` by setting `RemovedAtUtc = DateTimeOffset.UtcNow`.
*   **Unit Tests:** Every repository implementation must have a corresponding integration/unit test verifying `Save` and `Load` (hydration) logic against an in-memory or throwaway SQLite connection.
---

## Chunk 3 (Protobuf Contracts)
### Feature Implementation Request: Signal Protocol Chunk 3 (Network Definitions)
You are to implement all `.proto` contract updates required for the new Relay architecture. These changes establish the exact wire formats and gRPC service signatures without requiring any application-level business logic.

**1. Service Refactoring (`messaging.proto` & `internal_messaging.proto`)**
*   **Action:** Delete `FetchQueuedMessagesRequest`, `FetchQueuedMessagesResponse`, and `RelayOpaqueEnvelope` from `internal_messaging.proto`.
*   **Action:** Delete the obsolete `rpc FetchQueuedMessages` from `InternalMessagingService` in `internal_messaging.proto`.
*   **Action:** Ensure `RelayService` acts as the unified transport boundary. Remove any old endpoints like `StreamGroupMessages`, `Publish`, `ProvisionGroup`, or `ModifyGroup`.
*   **Action:** Delete `SubmitGroupMessageRequest`, `SubmitGroupMessageResponse`, `ProvisionGroupRequest`, `ModifyGroupRequest`, and `GroupProvisioningRequestedDomainEvent` representations if they exist.

**2. Group Egress & Relay API Contracts (`messaging.proto`)**
*   **Action:** Add the `AnonymousGroupService` containing a single unary endpoint for all operations:
    ```protobuf
    service AnonymousGroupService {
        rpc ProcessAnonymousGroupRequest(AnonymousGroupRequest) returns (ProcessAnonymousGroupResponse);
        rpc GetGroupState(GetGroupStateRequest) returns (GetGroupStateResponse);
    }
    ```
*   **Action:** Define the exact `AnonymousGroupRequest` message representing the blind transient fanout:
    ```protobuf
    message AnonymousGroupRequest {
        bytes conversation_id = 1;
        bytes presentation_proof = 2; // KVAC ZK Proof
        repeated bytes target_public_identity_ids = 3; // Transient Fanout list
        optional bytes ciphertext = 4; // Inner encrypted group message
        
        // Exclusively for State Mutations (Add/Kick/Rename)
        optional bytes new_encrypted_entries_blob = 5;
        optional uint32 new_epoch = 6;
        
        // Anti-Spam / Rate-limiting Header Note:
        // Client must provide grpc metadata header: "x-delivery-ticket" 
    }

    message ProcessAnonymousGroupResponse { 
        bool success = 1; 
    }
    ```
*   **Action:** Define `GetGroupState` messages for the ZK Anchor sync:
    ```protobuf
    message GetGroupStateRequest {
        bytes conversation_id = 1;
    }

    message GetGroupStateResponse {
        uint32 current_epoch = 1;
        bytes encrypted_entries_blob = 2;
    }
    ```

**3. Relay Ingress Delivery (`messaging.proto`)**
*   **Action:** Define `GroupMessageEnvelope` flowing from the Relay back to the peers over the authenticated stream:
    ```protobuf
    message GroupMessageEnvelope {
        bytes conversation_id = 1;
        uint32 sender_key_id = 2; 
        bytes ciphertext = 3;
    }
    ```

**4. Peer-to-Peer Payloads (`internal_messaging.proto`)**
*   **Action:** Define `GroupInitializationMessage` (sent over 1:1 sessions). This delivers the cryptographic primitives to new members:
    ```protobuf
    message GroupInitializationMessage {
        bytes conversation_id = 1;
        uint32 epoch = 2;
        bytes group_master_key = 3;
        bytes member_credential_mac = 4; // Replaces old "group_public_params"
        
        // --- Percolator-specific Relay Topology ---
        bytes relay_public_identity_id = 7;
        string relay_host = 8;
        int32 relay_port = 9;
    }
    ```
*   **Action:** Define `SenderKeyDistributionMessage` for ratchet key sharing:
    ```protobuf
    message SenderKeyDistributionMessage {
        bytes conversation_id = 1;
        uint32 sender_key_id = 2;
        bytes chain_key = 3;
        bytes signature_public_key = 4;
    }
    ```
*   **Action:** Define `SenderKeyRequest` for recovery:
    ```protobuf
    message SenderKeyRequest {
        bytes conversation_id = 1;
        uint32 sender_key_id = 2;
    }
    ```
*   **Action:** Define `GroupUpdatePayload` (encrypted payload representing semantic chat updates):
    ```protobuf
    message GroupUpdatePayload {
      optional string new_group_name = 1;
      optional bytes new_avatar_id = 2;
      repeated PeerRoleUpdate role_updates = 3;
      repeated bytes added_public_identity_ids = 4;
      repeated bytes removed_public_identity_ids = 5;
    }

    message PeerRoleUpdate {
      bytes public_identity_id = 1;
      uint32 role_enum = 2;
    }
    ```
*   **Action:** Wire these specific messages into the inner `DirectMessageContent` and `GroupContent` payload structures.

**5. Contract Generation Rules**
*   Ensure the C# Protobuf generated code uses the appropriate `Google.Protobuf` attributes. Do not implement any application services, queries, or handler logic in this chunk. Just compile the `.proto` files successfully.

**6. Ingress / Stream Operations (`messaging.proto`)**
*   **Action:** Define the new Bidirectional Stream contract. This handles standard 1:1 and the new Group V2 envelopes.
    ```protobuf
    rpc ConnectRelay(stream ClientRelayStream) returns (stream ServerRelayStream);

    message ClientRelayStream {
        oneof payload {
            MessageAck message_ack = 1;  // Acknowledges an ingress 1:1 or group message
        }
    }

    message MessageAck {
        optional bytes ack_id = 1; // Guid bytes
    }

    message ServerRelayStream {
        oneof payload {
            OpaqueMessageDelivery opaque_delivery = 1; // For 1:1 queued messages
            GroupMessageEnvelope group_message = 2; // For transient fan-out
        }
    }

    message OpaqueMessageDelivery {
        optional bytes ack_id = 1; // Guid bytes
        optional bytes opaque_payload = 2;
    }
    ```

**7. 1:1 Opaque Egress (`messaging.proto`)**
*   **Action:** Ensure `EnqueueOpaqueMessageRequest` represents the unified opaque drop-off for standard messages:
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

**5. Peer-to-Peer Payloads (`internal_messaging.proto`)**
*   **Action:** Define the exact payloads for establishing and modifying Group V2 E2EE sessions. Add these to `internal_messaging.proto`.
    ```protobuf
    // 1:1 Bootstrapping Payloads (Encrypted via Double Ratchet)
    message GroupInitializationMessage {
        optional bytes conversation_id = 1;
        optional uint32 epoch = 2;
        optional bytes group_master_key = 3;
        optional bytes group_public_params = 4;
        optional bytes encrypted_profile = 5;
        optional bytes member_credential = 6;
        optional bytes relay_public_identity_id = 7;
        optional string relay_host = 8;
        optional int32 relay_port = 9;
    }

    message SenderKeyDistributionMessage {
        optional bytes conversation_id = 1;
        optional uint32 sender_key_id = 2;
        optional bytes chain_key = 3;
        optional bytes signature_public_key = 4;
    }

    // Group Fan-out Payloads (Encrypted via Sender Key Ratchet)
    message GroupUpdatePayload {
        optional string new_group_name = 1;
        optional string new_description = 2;
        optional bytes new_avatar_id = 3;
        repeated PeerRoleUpdate role_updates = 4;
        repeated bytes added_public_identity_ids = 5;
        repeated bytes removed_public_identity_ids = 6;
    }

    // Envelope Wrapping
    message DirectMessageContent {
        // ... existing fields ...
        optional GroupInitializationMessage group_invite = 10;
        optional SenderKeyDistributionMessage sender_key_distribution = 11;
    }

    message GroupContent {
        optional string text_message = 1;
        optional GroupUpdatePayload update_payload = 2;
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

1. Protobuf Updates
* Implement the `.proto` message and service definitions from Chunk 3.

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