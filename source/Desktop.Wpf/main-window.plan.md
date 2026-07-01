
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
* Define `ISenderKeyCryptographyService` with a method to generate the sender key distribution message:
  ```csharp
  public interface ISenderKeyCryptographyService
  {
      SenderKeyDistributionMessageBytes CreateSenderKeyDistributionMessage(
          Percolator.Cryptography.Primitives.ConversationId conversationId,
          Percolator.Cryptography.Primitives.PeerId localPeerId,
          Percolator.Cryptography.Primitives.DeviceId deviceId);
  }
  ```
  * **Note:** The `PeerId` parameter in the Cryptography domain is a local-only identifier. When calling this from the Application layer, you will need to pass the local peer's `PeerId` from the Identity domain, converted to the Cryptography domain's `PeerId` primitive.

2. Domain Layer (`Percolator.Chat`)
* **Local Primitives:** Define `[ByteArray(minLength: 1, maxLength: 5000)] public sealed partial record ChatSenderKeyDistributionMessageBytes;` inside `Percolator.Chat.Messaging.ValueObjects` so the Chat domain doesn't depend on Cryptography.
* **Composite Identity Value Object:** Define `public sealed record GroupParticipantId(Pkh Pkh, ChatPeerId? LocalPeerId);` in `Percolator.Chat.GroupMembership`.
* **Domain Events Namespace:** Create a new folder `Percolator.Chat/Events` and define the following records implementing `Percolator.Identity.SeedWork.IDomainEvent`:
  * `public sealed record GroupProvisioningRequestedDomainEvent(ConversationId ConversationId, RelayGroupPublicParamsBytes PublicParams, IReadOnlyList<Pkh> MemberPkh);`
  * `public sealed record MemberInvitedDomainEvent(ConversationId ConversationId, GroupParticipantId ParticipantId, ChatSenderKeyDistributionMessageBytes DistributionMessage, Pkh RelayPkh);`
* **GroupConversation & GroupMember Refactor:**
  * Modify `Percolator.Chat.GroupMembership.GroupMember`: Change `ChatPeerId PeerId` to `GroupParticipantId ParticipantId`. Add `void ResolveLocalPeerId(ChatPeerId localId)` to upgrade an unresolved member.
  * Modify `Percolator.Chat.GroupLedger.GroupConversation`: Add `GroupParticipantId RelayIdentity` property. The group MUST know the globally verifiable routing identity of its host.
  * Add `private readonly List<IDomainEvent> _domainEvents = new();` to `GroupConversation`. Add `public IReadOnlyList<IDomainEvent> GetDomainEvents() => _domainEvents;` and `public void ClearDomainEvents() => _domainEvents.Clear();`.
  * Update `GroupConversation` constructor to accept the `RelayIdentity` and register a `GroupProvisioningRequestedDomainEvent` to `_domainEvents`.
  * Update `InviteMember(GroupParticipantId participantId, ChatSenderKeyDistributionMessageBytes distributionMessage)` to register a `MemberInvitedDomainEvent` to `_domainEvents`.

3. Message Queue Decoupling (`Percolator.Infrastructure`)
* **Infrastructure Bug Fix:** `MessageQueueItemDbo` currently uses `PeerId RecipientPeerId`. This strictly limits the queue to known local peers. You MUST refactor `MessageQueueItemDbo` to use `Pkh RecipientPkh` directly.
* **EF Core Value Converter Pattern:** In `PercolatorDbContext.OnModelCreating`, add a value converter for `Pkh`:
  ```csharp
  modelBuilder.Entity<MessageQueueItemDbo>()
      .Property(e => e.RecipientPkh)
      .HasConversion(
          v => v.ToArray(),
          v => Pkh.FromBytesOwned(v));
  ```
* **Repository Update:** Update `SqliteMessageQueueRepository.TryEnqueueAsync` to accept `Pkh recipientPkh` instead of `PeerId`. Update the internal counting logic to compare spans instead of GUIDs.

4. Relay Provisioning Ingress (`Percolator.Infrastructure/Network/Grpc/RelayGroupService.cs`)
* **The Provisioning Endpoint:** The Relay must expose a way to be provisioned blindly. Add `rpc ProvisionGroup(ProvisionGroupRequest) returns (ProvisionGroupResponse);` to `Percolator.Contracts/Protos/messaging.proto` under the `RelayGroupService` service definition.
* **Implementation:** Add `public override async Task<ProvisionGroupResponse> ProvisionGroup(ProvisionGroupRequest request, ServerCallContext context)` to `RelayGroupService`.
  * Validate inputs and parse bytes defensively using `.FromBytesOwned()`.
  * Define an orchestrator interface `IRelayGroupProvisioningOrchestrator` in `Percolator.Application.Chat` with method `Task ProvisionGroupAsync(ConversationId conversationId, RelayGroupPublicParamsBytes publicParams, IReadOnlyList<Pkh> memberPkh, CancellationToken ct)`. Implement it in `RelayGroupProvisioningOrchestrator`.
  * The orchestrator uses the existing `IRelayGroupLedgerRepository` to save the ledger state. You may need to extend `IRelayGroupLedgerRepository` with an `AddAsync` method if it doesn't exist, or use the repository pattern to directly insert `RelayGroupStateDbo` and `RelayBlindedRosterDbo` via `PercolatorDbContext`.
* **RelayBlindedRosterDbo Bug Fix:** The current `RelayBlindedRosterDbo` uses `Guid BlindedChatPeerId` which is a local-only concept. You MUST refactor it to use `Pkh MemberPkh` instead, with an EF Core Value Converter. This ensures the relay stores globally-routable PKHs for its blinded roster.

5. Infrastructure Layer (`Percolator.Infrastructure/Chat/Persistence`)
* **RelayOutboxDbo:** Create a DBO to store pending domain events: `Guid Id`, `string EventType`, `string PayloadJson`, `Pkh DestinationPkh`, `DateTimeOffset? ProcessedAtUtc`. Use an EF Core Value Converter for `DestinationPkh` (same pattern as MessageQueueItemDbo).
* **IGroupConversationRepository Update:** Implement `Task AddWithOutboxAsync(GroupConversation conversation, int selfIdentityId, CancellationToken ct)`.
  * In `SqliteGroupConversationRepository`, after saving the group state and members, iterate over `conversation.GetDomainEvents()`.
  * Serialize each event to JSON using `System.Text.Json.JsonSerializer.Serialize(event, event.GetType())`.
  * Map to `RelayOutboxDbo` and add to context.
  * Call `SaveChangesAsync` atomically.
  * Call `conversation.ClearDomainEvents()` after success.
* **IRemoteEnvelopeSender Extension:** The current `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync` requires `RecipientRoute(PeerId, IdentityPublicKeyHash?)`. Since we are moving to PKH-based routing for outbox events where we may not have a local `PeerId`, you MUST add an overload to `IRemoteEnvelopeSender`:
  * `Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, Pkh destinationPkh, CancellationToken ct = default);`
  * Implement this overload in the sender to look up the routing profile for the PKH and resolve the endpoint, or use the message queue if no direct session exists.
* **IPeerRoutingProfileRepository Extension:** The current `IPeerRoutingProfileRepository` is keyed by `PeerId`. To resolve relay endpoints from PKH in the outbox dispatcher, add a method:
  * `Task<PeerRoutingProfile?> GetByPkhAsync(Percolator.Chat.Messaging.ValueObjects.Pkh pkh, CancellationToken cancellationToken = default);`
  * Implement this in `SqlitePeerRoutingProfileRepository` by querying the `PeerRoutingProfileDbo` table and comparing the `PublicKeyHash` byte array to the PKH span.
* **OutboxDispatcherWorker:** An `IHostedService` that runs every 5 seconds. It queries the Outbox table for unprocessed events (where `ProcessedAtUtc` is null).
  * **Event Resolution:** Use a switch on `EventType` string to deserialize JSON back to the concrete domain event type.
  * **Provisioning Dispatch:** If it's a `GroupProvisioningRequestedDomainEvent`, invoke the Relay's `ProvisionGroup` gRPC endpoint. To resolve the relay endpoint from the `DestinationPkh`, inject `IPeerRoutingProfileRepository` and call `GetByPkhAsync(event.DestinationPkh, ct)`. Use `GrpcChannel.ForAddress(profile.Endpoints.First().Address)`.
  * **Invite Dispatch:** If it's a `MemberInvitedDomainEvent`, dispatch the `GroupInvite` via the new `IRemoteEnvelopeSender.SendChatEnvelopeToPeerAsync(chatEnvelope, event.DestinationPkh, ct)` overload. Construct the `ChatEnvelope` with the `GroupInvite` message populated from the event data.
* **Transient Network Backoff:** Wrap gRPC calls in try-catch for `RpcException`. If status is `Unavailable` or `DeadlineExceeded`, log warning, `await Task.Delay(TimeSpan.FromSeconds(5))`, and continue to next event without updating `ProcessedAtUtc`.

6. Application Orchestration (`Percolator.Application/Apps/Chat`)
* **Eliminate MediatR Indirection:** Since group creation is triggered from a single UI entry point, MediatR is unnecessary overhead. Delete the existing `CreateGroupCommand.cs` and `CreateGroupCommandHandler.cs` in `Percolator.Application/Apps/Chat`.
* **IGroupProvisioningAppService:** Create a new application service interface `Percolator.Application.Apps.Chat.IGroupProvisioningAppService` with a method `Task<ConversationId> ProvisionGroupAsync(string groupName, IReadOnlyList<Pkh> initialMembers, Pkh relayPkh, int selfIdentityId, CancellationToken ct);`. Implement it in `GroupProvisioningAppService`.
* **ProvisionGroupAsync Execution Steps:**
    1. Generate a 32-byte array of randomness using `RandomNumberGenerator.GetBytes(32)` and pass to `IGroupCryptographyService.GenerateGroupMasterKey()`.
    2. Derive `ZkGroupPublicParamsBytes` from the master key.
    3. Convert keys to Chat domain equivalents using zero-allocation span conversions:
       * `var chatMasterKey = Percolator.Chat.GroupLedger.GroupMasterKeyBytes.FromSpan(cryptoMasterKey.Span);`
       * `var chatPublicParams = Percolator.Chat.GroupLedger.RelayGroupPublicParamsBytes.FromSpan(cryptoPublicParams.Span);`
    4. Save the master key using `IGroupCryptoStateRepository.UpsertGroupMasterKeyAsync(conversationId, chatMasterKey, ct)`.
    5. Construct `GroupParticipantId` for the relay (`new GroupParticipantId(relayPkh, null)`) and initial members (`new GroupParticipantId(memberPkh, null)`).
    6. Generate a new `ConversationId` GUID for the group.
    7. Instantiate `Percolator.Chat.GroupLedger.GroupConversation` aggregate with:
       * The generated `ConversationId`
       * The relay's `GroupParticipantId`
       * The group name
       * This will raise a `GroupProvisioningRequestedDomainEvent` internally.
    8. For each member, generate distribution bytes via `ISenderKeyCryptographyService.CreateSenderKeyDistributionMessage()`. Convert to `ChatSenderKeyDistributionMessageBytes` using `FromSpan()` and invoke `group.InviteMember()`. This will raise `MemberInvitedDomainEvent` for each member.
    9. Save atomically via `await _groupConversationRepository.AddWithOutboxAsync(group, selfIdentityId, ct)`.
    10. Return the generated `ConversationId`.

7. Integration Anchor: Provisioning & Invite Ingress (`Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`)
* Update `ProcessInternalEnvelopeHandler` to include a switch case for `ChatEnvelope.MessageOneofCase.GroupInvite`.
* **IGroupInviteHandler Interface:** Define a new interface in `Percolator.Application.Chat`:
  ```csharp
  public interface IGroupInviteHandler
  {
      Task HandleGroupInviteAsync(GroupInvite invite, int selfIdentityId, CancellationToken ct);
  }
  ```
* **Implementation:** Implement `GroupInviteHandler` with the following steps:
  1. Parse the Protobuf `ByteString` fields into proper Domain Value Objects using defensive copies:
     * `var conversationId = new Percolator.Chat.Messaging.ValueObjects.ConversationId(new Guid(invite.ConversationId.ToByteArray()));`
     * `var groupMasterKey = Percolator.Cryptography.GroupMasterKey.FromBytesOwned(invite.GroupMasterKey.ToByteArray());`
     * `var relayPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytesOwned(invite.RelayPublicKeyHash.ToByteArray());`
     * `var inviterPkh = Percolator.Chat.Messaging.ValueObjects.Pkh.FromBytesOwned(invite.InviterPkh.ToByteArray());`
     * `var distributionMessage = Percolator.Chat.Messaging.ValueObjects.ChatSenderKeyDistributionMessageBytes.FromBytesOwned(invite.SenderKeyDistributionMessage.ToByteArray());`
  2. Convert the cryptography domain `GroupMasterKey` to Chat domain: `var chatMasterKey = Percolator.Chat.GroupLedger.GroupMasterKeyBytes.FromSpan(groupMasterKey.Span);`
  3. Persist the master key using `IGroupCryptoStateRepository.UpsertGroupMasterKeyAsync(conversationId, chatMasterKey, ct)`.
  4. Process the sender key distribution message using the cryptography service (this will update the local sender key state).
  5. Initialize a local `GroupConversation` aggregate with:
     * The parsed `ConversationId` from the invite
     * `RelayIdentity = new GroupParticipantId(relayPkh, null)` (initially unresolved)
     * Add the inviter as a member using `GroupParticipantId(inviterPkh, null)`
  6. Save the conversation via `IGroupConversationRepository.AddAsync`.
* **Handler Injection:** In `ProcessInternalEnvelopeHandler`, inject `IGroupInviteHandler` and call it for the `GroupInvite` case.

8. Network Contracts (`internal_messaging.proto` & `messaging.proto`)
* **internal_messaging.proto:** 
  * The existing `GroupKeyBootstrap` message (field 15) is used for bootstrapping the master key. You MUST create a NEW message `GroupInvite` for the full invitation protocol:
    ```protobuf
    message GroupInvite {
      optional uint32 version = 100;
      optional bytes conversation_id = 1; // GUID bytes
      optional bytes group_master_key = 2; // 32-byte master key
      optional bytes sender_key_distribution_message = 3; // Sender key distribution
      optional bytes inviter_pkh = 4; // Public key hash of inviter
      optional bytes relay_public_key_hash = 5; // Public key hash of relay
    }
    ```
  * Change `create_group = 16` to `group_invite = 16` in the `ChatEnvelope` oneof configuration.
* **messaging.proto:**
  * Add to the existing `RelayGroupService` definition: `rpc ProvisionGroup(ProvisionGroupRequest) returns (ProvisionGroupResponse);`
  * Define `ProvisionGroupRequest`: `optional bytes conversation_id = 1; optional bytes group_public_params = 2; repeated bytes member_public_key_hashes = 3;`.
    * **Note:** `conversation_id` is a `bytes` field in protobuf but maps to `ConversationId(Guid Value)` in the code. Parse it as: `var conversationId = new Percolator.Cryptography.Primitives.ConversationId(new Guid(request.ConversationId.ToByteArray()));`.
  * Define `ProvisionGroupResponse`: `optional bool success = 1;`.

**Testing Requirements (Chunk 5):**
- `MessageQueueItemDbo_EnqueuesByPkh_Successfully` - Test that the message queue can accept envelopes for PKHs that do not exist in the local `PeerIdentityDbo` database.
- `GroupProvisioningAppService_ProvisionGroupAsync_GeneratesValidOutboxEvents` - Test that creating a group yields one provisioning event for the relay and one invite event per member.
- `RelayGroupService_ProvisionGroup_CreatesLedgerAndRoster_FromPkhs` - Test that the Relay correctly initializes the `RelayGroupStateDbo` and `RelayBlindedRosterDbo` using the raw PKHs.

---

## Chunk 5.1 (Envelope Context Bridging)
### Feature Implementation Request: Signal Protocol Chunk 5.1 (InternalEnvelope Sender Context)
The Goal: To align with Signal's architecture, the internal unencrypted envelope must explicitly carry the Sender's UUID and Device ID. This prevents inner payloads (like `GroupInvite`) from needing to transport this data, while enabling proper `SenderAddress` construction for the cryptographic VTable.

Implementation Requirements:

1. Network Contracts (`internal_messaging.proto`)
* Update `InternalEnvelope` in `Percolator.Contracts/Protos/internal_messaging.proto` to include the sender context:
  ```protobuf
  message InternalEnvelope {
    optional bytes source_peer_id = 100;    // 16-byte UUID of the sender
    optional uint32 source_device_id = 101; // Sender's device ID (e.g., 1)
    oneof application_payload {
      // ... existing payloads
    }
  }
  ```

2. Application Orchestration (`Percolator.Application`)
* **Session Context Update:** Modify `Percolator.Application.Network.ProcessInternalEnvelopeCommand.SessionContext` to include `uint? SourceDeviceId` alongside the existing properties.
* **Decryption Ingress Update:** In `Percolator.Application.Network.DeliverOpaqueMessageHandler` (and any other entry point that decrypts `SessionRatchetMessage` into an `InternalEnvelope`), extract the `source_device_id` (defaulting to 1 if not set) and `source_peer_id` from the decrypted `InternalEnvelope` and populate the `SessionContext` when dispatching `ProcessInternalEnvelopeCommand`.
* **Outbound Egress Update:** In `Percolator.Application.Network.RemoteEnvelopeSender` (or equivalent outbound paths that wrap payloads in `InternalEnvelope` before encryption), you MUST populate `source_peer_id` with the local self identity's `PeerId` and `source_device_id` with the local device ID.

3. Group Invite Integration (`Percolator.Application/Chat`)
* **Handler Interface:** Update `IGroupInviteHandler.HandleGroupInviteAsync` to accept the `uint sourceDeviceId` as a parameter.
* **Process Handler Update:** In `ProcessInternalEnvelopeHandler`, extract `request.Context.SourceDeviceId` (fallback to 1 if null) and pass it into the `HandleGroupInviteAsync` call for the `GroupInvite` branch.
* **Cryptography Integration:** In `GroupInviteHandler`, use the provided `sourceDeviceId` to initialize `var senderDeviceId = new Percolator.Cryptography.Primitives.DeviceId(sourceDeviceId);`. Pass this `senderDeviceId` alongside the `inviterPkh` into `ISenderKeyCryptographyService.ProcessSenderKeyDistributionMessage(...)` to correctly initialize the native `SenderKeyStore`.

---

## Chunk 5.2 (Database Schema Refactoring for PeerId)
### Feature Implementation Request: Signal Protocol Chunk 5.2 (PeerId Surrogate Key Migration)
The Goal: Refactor the database schema to use auto-incrementing uint surrogate keys for peer relationships. This aligns with database normalization best practices. Guid IDs are being completely dropped as a concept from all tables except for the local self identity.

**Architectural Context:**
- Current state: Many tables use Guid columns as Primary Keys and Foreign Keys for peer relationships
- Problem: Guids as PKs cause index fragmentation and increase FK column size
- Solution: Use uint auto-increment PKs for local database mechanics

**CRITICAL EXCEPTION:**
- `Percolator.Infrastructure.Persistence.SelfIdentityDbo.PeerId` MUST remain a Guid - this is the global Signal UUID for the local identity only
- All other PeerId columns will be changed to uint columns
- NO GlobalPeerId columns will be added to any tables

**Implementation Approach:**
- No data migrations will be performed
- Make the schema changes and accept compiler errors
- Fix compiler errors in subsequent chunks

**Candidate PeerId Guid Columns (Fully Qualified):**

**Primary Keys (Guid) that should become uint PKs:**
1. `Percolator.Infrastructure.Identity.PeerIdentityDbo.PeerId` - Currently the authoritative peer catalog PK
2. `Percolator.Infrastructure.Persistence.PeerRoutingProfileDbo.PeerId` - Network routing profile PK

**Foreign Keys (Guid) that should reference uint PKs:**
3. `Percolator.Infrastructure.Persistence.DirectSessionDbo.RemotePeerId` - References remote peer
4. `Percolator.Infrastructure.Cryptography.PendingSessionDbo.RemotePeerId` - References remote peer
5. `Percolator.Infrastructure.Cryptography.PendingSessionDbo.RelayHostPeerId` - References relay host peer
6. `Percolator.Infrastructure.Persistence.SessionDbo.RemotePeerId` - References remote peer
7. `Percolator.Infrastructure.Identity.PeerIdentityKeyDbo_V2.PeerId` - FK to PeerIdentityDbo
8. `Percolator.Infrastructure.Persistence.PeerPublicSigningKeyDbo.PeerId` - FK to peer catalog
9. `Percolator.Infrastructure.Chat.Persistence.SenderKeyRecordDbo.SenderPeerId` - Signal sender key PK component
10. `Percolator.Infrastructure.Persistence.SelfIdentityKnownPeerDbo.PeerId` - FK to peer catalog
11. `Percolator.Infrastructure.Cryptography.SentInvitationDbo.TargetPeerId` - Target peer reference
12. `Percolator.Infrastructure.Cryptography.SentInvitationDbo.InviteRelayHostPeerId` - Relay host reference
13. `Percolator.Infrastructure.Persistence.PeerRouteCandidateDbo.RemotePeerId` - Routing candidate
14. `Percolator.Infrastructure.Persistence.PeerRouteCandidateDbo.RelayHostPeerId` - Relay host reference
15. `Percolator.Infrastructure.Persistence.RelayLinkDbo.PeerId` - FK to PeerRoutingProfileDbo
16. `Percolator.Infrastructure.Persistence.RelayLinkDbo.RelayPeerId` - FK to PeerRoutingProfileDbo
17. `Percolator.Infrastructure.Persistence.GrpcEndPointRoutingDbo.PeerId` - FK to PeerRoutingProfileDbo
18. `Percolator.Infrastructure.Persistence.DiscoveredPeerDbo.BoundPeerId` - DHT discovery binding

**Implementation Requirements:**

1. **Schema Migration Strategy**
   - Change `PeerIdentityDbo.PeerId` from Guid PK to uint PK (ValueGeneratedNever - app-assigned)
   - Change `PeerRoutingProfileDbo.PeerId` from Guid PK to uint PK (ValueGeneratedNever - matches PeerIdentityDbo.PeerId)
   - Change all FK columns from Guid to uint
   - Update EF Core model configuration to reflect new PK/FK structure
   - DO NOT create data migration scripts

2. **Domain Layer Updates**
   - Update repository interfaces to accept uint for local operations where appropriate
   - Update cryptography layer to use SelfIdentityDbo.PeerId (Guid) when constructing Signal Protocol addresses for local identity
   - For remote peers, the uint PK will be used for local lookups

3. **Testing Requirements**
   - Verify all FK relationships still function correctly after migration
   - Test that Signal Protocol operations use SelfIdentityDbo.PeerId correctly for local identity
   - Ensure backward compatibility with any existing serialized data

---

## Chunk 5.2.a (Domain Type Refactoring for PeerId)
### Feature Implementation Request: Signal Protocol Chunk 5.2.a (PeerId Domain Type Migration)
The Goal: Refactor all domain types that wrap PeerId as a Guid to instead wrap uint as simple DDD value types. This aligns the domain layer with the database schema changes in Chunk 5.2.

**Standard PeerId Definition (uint):**
All domains will use the same simple definition:
```csharp
public readonly record struct PeerId(uint Value)
{
    public override string ToString() => Value.ToString();
}
```

**Fully Qualified Domain Types (Guid → uint):**

1. `Percolator.Cryptography.Primitives.PeerId` - Replace with standard definition above
2. `Percolator.Identity.PeerId` - Replace with standard definition above
3. `Percolator.Network.PeerId` - Replace with standard definition above
4. `Percolator.Chat.GroupMembership.ChatPeerId` - Replace with standard definition above

**Implementation Requirements:**

1. **Type Definition Updates**
   - Replace each domain type's definition with the standard PeerId definition above
   - Ensure all domains use the exact same definition for consistency

2. **Usage Updates**
   - Update all usages of these types to pass uint values
   - Remove any conversion methods to/from Guid (no longer needed)
   - Update serialization/deserialization logic if present

3. **Testing Requirements**
   - Verify all domain type tests pass with uint values
   - Ensure equality comparisons work correctly with uint
   - Test that the same definition works across all domains

---
## Chunk 5.3 - PeerIdentity Wire Identity Synchronization

The Goal: Currently, the system assumes a `PeerIdentity` can be created using a locally generated `PeerId` without requiring a `PublicIdentityId`. However, the desired reality is that peers must generate their own `PublicIdentityId` (their `SelfIdentity.PublicIdentityId`) and transmit it over the wire during the initial handshake process. We need to update the protobuf messages, sender logic, and receiver logic to ensure that `PublicIdentityId` is always provided by the remote peer and correctly persisted.

**Architectural Constraint (Signal Alignment):**
In Signal, your identity is NOT bound to an ephemeral session or a mutable cryptographic key. Your identity is your UUID (represented locally as `PublicIdentityId`). The `PeerId` is strictly a local database surrogate key (uint) to make indexing faster. Therefore, you cannot "create" a peer identity by generating a local UUID. The UUID *is* the peer.

**1. `IPeerIdentityRepository` Refactor**
*   **Remove** `GetByNameAsync(DisplayName name)`. As the application should never look up peers by display name (it is an existing method currently only used by tests), this method is a design smell and should be removed. Fix any integration tests to look up by `PeerId` or `PublicIdentityId` instead.
*   **Keep** `GetByIdAsync(PeerId id)` as it is still needed for internal database relationships where the local surrogate key is used (e.g. `ProfileOrchestrationService`).
*   **Add** `Task<PeerIdentity?> GetByPublicIdentityIdAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default);`
*   **Add** `Task<PeerIdentity> GetOrCreateAsync(PublicIdentityId publicIdentityId, CancellationToken ct = default);`
    *   This is crucial: The repository itself should be responsible for allocating the local `PeerId` (uint) when a new `PublicIdentityId` is encountered. The application layer shouldn't call `PeerId.NewId()`.
*   Update `SqlitePeerIdentityRepository.cs` to implement these changes. When `GetOrCreateAsync` creates a new row, it will insert a new `PeerIdentityDbo` setting the `PublicIdentityId` and allowing the database to generate the `PeerId` identity column, returning the fully hydrated aggregate.

**2. Protobuf Updates:**
*   Modify `Percolator.Contracts/Protos/messaging.proto`:
    *   In `EstablishSessionRequest`, add `optional bytes public_identity_id = 6;` (16 bytes UUID representing the inviter's `SelfIdentity.PublicIdentityId`).
    *   In `EstablishSessionResponse.Response.ResponsePayload`, add `optional bytes public_identity_id = 4;` (16 bytes UUID representing the acceptor's `SelfIdentity.PublicIdentityId`).
*   Modify `Percolator.Contracts/Protos/internal_messaging.proto`:
    *   In `InviteHandshakeRequestPayload`, add `optional bytes inviter_public_identity_id = 7;`.
    *   In `InviteHandshakeResponse`, add `optional bytes acceptor_public_identity_id = 6;`.

**3. Sender Side Updates (Client):**
*   Update `EstablishDirectSessionService.cs` (when acting as inviter building `InviteHandshakeRequestPayload`) to include the local `SelfIdentity.PublicIdentityId` in the outgoing payload.
*   Update `InitiatorFinalizeService.cs` (when acting as acceptor sending `InviteHandshakeResponse`) to include the local `SelfIdentity.PublicIdentityId`.
*   Update any standard handshake egress logic to include the `public_identity_id`.

**4. Receiver Side Updates (Server):**
*   Update `EstablishDirectSessionService.cs` (when receiving an invitation):
    *   Extract `inviter_public_identity_id` from the decoded `InviteHandshakeRequestPayload`.
    *   Call `await _peerIdentityRepository.GetOrCreateAsync(new PublicIdentityId(new Guid(payload.InviterPublicIdentityId.ToByteArray())))`.
*   Update `InitiatorFinalizeService.cs` (when receiving `InviteHandshakeResponse`):
    *   Extract `acceptor_public_identity_id` from the response.
    *   Call `await _peerIdentityRepository.GetOrCreateAsync(...)`.
*   Update `StandardHandshakeIngress.cs` (when receiving `EstablishSessionRequest`):
    *   Extract `public_identity_id` from the request.
    *   Call `await _peerIdentityRepository.GetOrCreateAsync(...)`.

**5. Test Updates:**
*   Fix all unit tests. Remove calls to `PeerId.NewId()` in the application layer tests.


## Chunk 5.3.a - Identity Key vs PublicIdentityId Implementation Plan

The Goal: Modify network contracts to explicitly replace Public Key Hashes (PKH) with `PublicIdentityId` (UUID) for addressing and routing. This aligns with Signal's architecture where UUIDs are the stable identifier, allowing Identity Keys to rotate without breaking group memberships.

**1. Update `messaging.proto`**
*   File: `Percolator.Contracts/Protos/messaging.proto`
*   In `ProvisionGroupRequest`, change `repeated bytes member_pkh = 3;` to `repeated bytes member_public_identity_ids = 3;`.

**2. Update `internal_messaging.proto`**
*   File: `Percolator.Contracts/Protos/internal_messaging.proto`
*   In `GroupInvite`, replace `optional bytes inviter_pkh = 2;` with `optional bytes inviter_public_identity_id = 2;`.
*   In `GroupInvite`, replace `optional bytes relay_pkh = 5;` with `optional bytes relay_public_identity_id = 5;`.

**3. Update Message Instantiations (Sender Side)**
Using compiler errors and search, find the protobuf instantiations of the above messages and swap out the PKH bytes for the `PublicIdentityId.Value.ToByteArray()` bytes.

*   **EstablishSessionRequest**:
    *   `Percolator.Application\Cli\RequestPreKeyBundleByPkhHandler.cs`: `new EstablishSessionRequest`
    *   `Percolator.Application\Network\Handshake\ProcessRelayedOpaquePayloadCommand.cs`: `new EstablishSessionRequest`
    *   `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs`: `new EstablishSessionRequest`
    *   `Percolator.Cryptography\HandshakeInvitation.cs`: `new EstablishSessionRequest`
*   **EstablishSessionResponse / ResponsePayload**:
    *   `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs`: `new EstablishSessionResponse.Types.Response.Types.ResponsePayload`
*   **InviteHandshakeRequestPayload**:
    *   `Percolator.Application\Network\MainReverseSignalInviteFactory.cs`: `new InviteHandshakeRequestPayload`
    *   `Desktop.Wpf\Features\Simulator\SimulatedPeerItemViewModel.cs`: `new InviteHandshakeRequestPayload`
    *   `Desktop.Wpf\Features\Simulator\SimulatedPeerCardViewModel.cs`: `new InviteHandshakeRequestPayload`
    *   `Desktop.Wpf\Features\Simulator\SimulatedHandshakeStateMachineCardViewModel.cs`: `new InviteHandshakeRequestPayload`
*   **InviteHandshakeResponse**:
    *   `Percolator.Application\Network\ApprovePendingSessionCommand.cs` (`ApprovePendingSessionHandler`): `new InviteHandshakeResponse`
    *   `Desktop.Wpf\Features\Simulator\SimulatorStateService.cs`: `new InviteHandshakeResponse`

      *   `Percolator.Application\Handshake\InitiatorFinalizeService.cs`: `new InviteHandshakeResponse`

## Chunk 5.3.b - Include Relay Endpoint in Group Provisioning/Invites

The Goal: In a P2P environment without a central server, when a user is invited to a group hosted on a specific relay, the invitee MUST know the network address (hostname and port) of the relay to establish a connection. Currently, `GroupInvite` only includes the relay's `PublicIdentityId`, which is insufficient if the invitee has never encountered the relay before. We need to append relay endpoint info to the invite message.

CRITICAL ARCHITECTURAL REQUIREMENT: The `Chat` domain (`GroupConversation`, `MemberInvitedDomainEvent`, etc.) MUST NOT know anything about hostnames, IP addresses, or ports. These are network-layer concerns. The `Chat` domain is only concerned with abstract identifiers like `PeerId` and `PublicIdentityId`.

**1. Update `internal_messaging.proto`**
*   File: `Percolator.Contracts/Protos/internal_messaging.proto`
*   In `GroupInvite`, append the relay network coordinates:
    *   `optional string relay_host = 7;`
    *   `optional int32 relay_port = 8;`

**2. Update Outbox Dispatcher (Sender Side - Infrastructure)**
*   File: `Percolator.Infrastructure/Outbox/OutboxDispatcherWorker.cs`
*   Inject `IPeerRoutingProfileRepository` into `OutboxDispatcherWorker`.
*   In `DispatchEventAsync`, for the `MemberInvitedDomainEvent` case:
    1.  The event contains the `RelayPeerId` (which is a `uint` wrapped in `ChatPeerId`).
    2.  Query `IPeerRoutingProfileRepository.GetByIdAsync(new Percolator.Network.PeerId(inviteEvent.RelayPeerId.Value), ct)`.
    3.  Extract the relay's host and port. Look at `profile.Endpoints.FirstOrDefault()?.EndPoint` (which is a `DnsEndPoint`).
    4.  *(When implemented)* Populate the new `relay_host` and `relay_port` fields on the `GroupInvite` protobuf message before sending it via `IRemoteEnvelopeSender`.

**3. Update Receiver Logic (Application)**
*   File: `Percolator.Application/Chat/GroupInviteHandler.cs`
*   Inject `IPeerRoutingProfileRepository` into `GroupInviteHandler`.
*   When extracting the `GroupInvite`, check if `relay_host` and `relay_port` are provided.
*   If provided, construct a `DnsEndPoint`.
*   Fetch the relay's profile using `IPeerRoutingProfileRepository.GetByIdAsync(relayPeerId)` (where `relayPeerId` is the network `PeerId` derived from the `relayIdentity`).
*   If the profile exists, call `AddGrpcEndPoint` with the endpoint and current UTC time, then `UpsertAsync`.
*   If it doesn't exist, create a new `PeerRoutingProfile`, bind the identity, set the endpoint, and save it. (This ensures the local networking layer knows how to reach the newly discovered relay).

## Chunk 5.4 - Fix Group Member Identity Resolution using Polymorphic ParticipantId

The Goal: Currently, the codebase is in a transitional state. We updated domain events and network contracts to use universal `PublicIdentityId`s (UUIDs), but the `GroupConversation` domain object and its internal `GroupMember` still rely on `ChatPeerId` (which wraps a local database `PeerId`). This is structurally incorrect because the user's *own* local identity (`SelfIdentity`) does not have a `PeerId`. 

To solve this while avoiding N+1 database queries downstream, we will introduce a discriminated union `ParticipantId` type in the Chat domain. This type will act as a memoized identity resolution token carrying the universal UUID alongside the localized database surrogate key.

**1. Update Domain Layer (`Percolator.Chat`)**
*   **Remove** usage of raw `ChatPeerId` for group members.
*   **Create `ChatSelfId.cs`**:
    *   File: `Percolator.Chat/GroupMembership/ChatSelfId.cs`
    *   Create a simple wrapper: `public readonly record struct ChatSelfId(uint Value);`
*   **Create `ParticipantId.cs`**:
    *   File: `Percolator.Chat/GroupMembership/ParticipantId.cs`
    *   Implement the discriminated union:
    ```csharp
    public abstract record ParticipantId(PublicIdentityId PublicIdentityId);

    public sealed record RemoteParticipantId(PublicIdentityId PublicIdentityId, ChatPeerId PeerId) 
        : ParticipantId(PublicIdentityId);

    public sealed record LocalParticipantId(PublicIdentityId PublicIdentityId, ChatSelfId SelfId) 
        : ParticipantId(PublicIdentityId);
    ```
*   **Update `GroupMember.cs`**:
    *   Change `ChatPeerId PeerId` property to `ParticipantId ParticipantId`.
*   **Update `GroupConversation.cs`**:
    *   Keep `ChatPeerId RelayPeerId` (no change to relay field type).
    *   Update constructor, `AddMember`, `InviteMember`, and `RemoveMember` to take `ParticipantId` instead of `ChatPeerId`.
*   **Update Domain Events**:
    *   Update `MemberInvitedDomainEvent.cs` and `GroupProvisioningRequestedDomainEvent.cs` to use `ParticipantId` for members and `PublicIdentityId` for the relay.

**2. Update Persistence Layer (`Percolator.Infrastructure.Chat`)**
*   **Update `GroupMemberDbo.cs`**:
    *   Add `public PublicIdentityId PublicIdentityId { get; set; }` (Required) using the chat domain type.
    *   Change `PeerId PeerId` to `public uint? PeerId { get; set; }`.
    *   Add `public uint? SelfId { get; set; }`.
*   **Update `PercolatorDbContext.cs`**:
    *   Locate the `modelBuilder.Entity<Percolator.Infrastructure.Chat.Persistence.GroupMemberDbo>` configuration block.
    *   Change the primary key from `entity.HasKey(e => new { e.ConversationId, e.PeerId });` to `entity.HasKey(e => new { e.ConversationId, e.PublicIdentityId });`.
    *   Add conversion for `PublicIdentityId` property to map between chat domain type and Guid:
        ```csharp
        entity.Property(e => e.PublicIdentityId)
            .HasConversion(
                v => v.Value,
                v => new PublicIdentityId(v))
            .IsRequired();
        ```
*   **Update `SqliteGroupConversationRepository.cs`**:
    *   When pulling from the DB (`ToDomain`), instantiate the correct `ParticipantId` subclass: if `PeerId` is not null, return `RemoteParticipantId`; if `SelfId` is not null, return `LocalParticipantId`.
    *   When persisting (`AddAsync`/`UpdateAsync`), populate `GroupMemberDbo.PublicIdentityId`, and assign the correct nullable `PeerId` or `SelfId` depending on the subclass pattern match.
    *   **Outbox Routing**: When constructing outbox messages, use pattern matching on the domain event's `ParticipantId`. If it's a `RemoteParticipantId`, use its `PeerId` for `DestinationPeerId`.

**3. Update Outbound Paths (Application Layer)**
*   **Update `IGroupProvisioningAppService.cs` & `GroupProvisioningAppService.cs`**:
    *   Change the signature to accept `IReadOnlyList<ParticipantId> invitees` and `PublicIdentityId relayIdentity`. The caller (CLI/UI layer) is now responsible for providing the fully resolved `ParticipantId` unions.
*   **Update `SendGroupMessageCommandHandler.cs`**:
    *   When establishing the network route `RecipientRoute`, resolve the `group.RelayPublicIdentityId` to a network `PeerId`.

**4. Update Inbound Paths & Messaging (Application Layer)**
*   **Update `IChatMessageWriter.cs` & `SqliteChatMessageWriter.cs`**:
    *   Update methods (`AddTextMessageAsync`, `AddReadReceiptAsync`, etc.) to take `ParticipantId` instead of `ChatPeerId`.
*   **Update Message Commands (`ReceiveTextMessageCommand.cs`, etc.)**:
    *   Update incoming mediatR commands and their handlers to carry the new `ParticipantId` instead of `ChatPeerId`.
*   **Update `GroupInviteHandler.cs`**:
    *   When parsing the `GroupInvite` protobuf, use `IPeerIdentityRepository` and `ISelfIdentityQueries` to map the raw `inviter_public_identity_id` into a `RemoteParticipantId` and the local identity into a `LocalParticipantId` before inserting them into `GroupConversation`.

**5. Testing & Validation**
*   Accept compiler errors and fix them across the test suite by updating mock setups to pass `ParticipantId` unions.
*   Run the EF Core migration to verify `GroupMemberDbo` pk migration succeeds.

## Chunk 5.4.a - Review Percolator.Chat Library for uint/int SelfIdentityId Usage

The Goal: After defining the `ChatSelfId` domain type in Chunk 5.4, we must review the Percolator.Chat library for internal usages of raw `uint` or `int` selfIdentityId. These should be updated to use the new `ChatSelfId` type for type safety and consistency within the domain library.

**Candidates for Review (Internal to Percolator.Chat):**

**1. Repository Interfaces (`Percolator.Chat` root)**
*   `IGroupConversationRepository.cs`:
    *   Method signatures use `uint selfIdentityId` (lines 11-13, 18).
*   `IMessageRepository.cs`:
    *   Method signatures use `int selfIdentityId` (lines 12-14).
*   `IDirectConversationRepository.cs`:
    *   Method signatures use `int selfIdentityId` (lines 11-14).

**2. Messaging Application Interfaces (`Percolator.Chat.Messaging.App`)**
*   `IChatMessageWriter.cs`:
    *   All method signatures use `uint selfIdentityId` (lines 10, 19, 27, 35).
*   `IConversationResolver.cs`:
    *   `DirectConversationResolution` record uses `uint SelfIdentityId` (line 13).

**3. Messaging Domain Events (`Percolator.Chat.Messaging.Events`)**
*   `DeliveredReceiptReceivedEvent.cs`:
    *   Property `SelfIdentityId` is `uint` (line 11, constructor line 19, 26).
*   `EmojiAnnotationReceivedEvent.cs`:
    *   Property `SelfIdentityId` is `uint` (line 10, constructor line 18, 25).
*   `ReadReceiptReceivedEvent.cs`:
    *   Property `SelfIdentityId` is `uint` (line 10, constructor line 17, 23).
*   `TextMessagePostedEvent.cs`:
    *   Property `SenderSelfIdentityId` is `uint` (line 12, constructor line 21, 29).
*   `TextMessageReceivedEvent.cs`:
    *   Property `SelfIdentityId` is `uint` (line 11, constructor line 20, 28).

**4. Messaging Application Commands (`Percolator.Chat.Messaging.App.Commands`)**
*   `UpdateGroupInfoCommand.cs`:
    *   Property `SelfIdentityId` is `int` (line 8).

**5. Messaging Application Handlers (`Percolator.Chat.Messaging.App.Handlers`)**
*   `PostDeliveredReceiptHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) and casts to `ChatPeerId` (lines 31, 40).
*   `PostReadReceiptHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) and casts to `ChatPeerId` (lines 31, 41).
*   `PostEmojiAnnotationHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) and casts to `ChatPeerId` (lines 31, 42).
*   `ReceiveEmojiAnnotationHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) (lines 31, 41).
*   `ReceiveReadReceiptHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) (lines 31, 40).
*   `ReceiveTextMessageHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) (lines 32, 42).
*   `ReceiveDeliveredReceiptHandler.cs`:
    *   Uses `resolution.SelfIdentityId` (uint) (lines 32, 41).
*   `UpdateGroupInfoHandler.cs`:
    *   Uses `request.SelfIdentityId` (int) (lines 17, 25).

**Critical Semantic Rule:** `ChatPeerId` and `ChatSelfId` are semantically distinct types representing different identity concepts (remote peer vs. local self). These two uint values must never be converted or used interchangeably. If any code attempts to convert a `ChatPeerId` to a `ChatSelfId` or vice versa, the AI must stop immediately and ask the user for direction on how to handle this semantic mismatch.

**Note:** This chunk is for informational purposes to guide the refactoring. External compiler errors (outside Percolator.Chat) are acceptable and will be addressed in subsequent chunks by the consuming application layers.

## Chunk 5.4.b - Border Conversion Points: Identity Domain SelfId to Chat Domain ChatSelfId

The Goal: Identify all public methods in Percolator.Chat that are called from outside the library (from Percolator.Application, Desktop.Wpf, etc.) where the Identity domain's `SelfId` (uint) must be converted to the Chat domain's `ChatSelfId` at the domain boundary. These are the integration points where the type conversion must occur after `ChatSelfId` is defined in Chunk 5.4.

**Repository Interface Methods (Called from Percolator.Application):**

**1. `IGroupConversationRepository`**
*   **Method**: `GetByIdAsync(ConversationId conversationId, uint selfIdentityId, CancellationToken ct)`
*   **External Callers**:
    *   `SendGroupMessageCommandHandler.cs` (line 50): `GetByIdAsync(request.ConversationId, request.SelfIdentityId, ...)`
    *   **Conversion Required**: `request.SelfIdentityId` (Identity domain `SelfId`) → `ChatSelfId`
*   **Method**: `AddWithOutboxAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken ct)`
*   **External Callers**:
    *   `GroupProvisioningAppService.cs` (line 97): `AddWithOutboxAsync(groupConversation, selfIdentityId, ...)`
    *   **Conversion Required**: `selfIdentityId` (Identity domain `SelfId`) → `ChatSelfId`
*   **Method**: `AddAsync(GroupConversation conversation, uint selfIdentityId, CancellationToken ct)`
*   **External Callers**:
    *   `GroupInviteHandler.cs` (line 169): `AddAsync(groupConversation, selfIdentityId.Value, ...)`
    *   **Conversion Required**: `selfIdentityId.Value` (Identity domain `SelfId`) → `ChatSelfId`

**2. `IChatMessageWriter`**
*   **Method**: `AddTextMessageAsync(ConversationId conversationId, uint selfIdentityId, ChatPeerId senderId, ...)`
*   **External Callers**:
    *   `SendGroupMessageCommandHandler.cs` (line 81): `AddTextMessageAsync(..., request.SelfIdentityId, ...)`
    *   `PostTextMessageHandler.cs` (line 39): `AddTextMessageAsync(..., resolution.SelfIdentityId, ...)`
    *   `ProcessInternalEnvelopeHandler.cs` (line 479): `AddTextMessageAsync(..., request.Context.SelfIdentityId.Value, ...)`
    *   **Conversion Required**: All `selfIdentityId` parameters (Identity domain `SelfId`) → `ChatSelfId`
*   **Method**: `AddReadReceiptAsync(ConversationId conversationId, uint selfIdentityId, ChatPeerId readerId, ...)`
*   **External Callers**: None found in current codebase (internal only)
*   **Method**: `AddEmojiAnnotationAsync(ConversationId conversationId, uint selfIdentityId, ChatPeerId reactorId, ...)`
*   **External Callers**: None found in current codebase (internal only)
*   **Method**: `AddDeliveredReceiptAsync(ConversationId conversationId, uint selfIdentityId, ChatPeerId recipientId, ...)`
*   **External Callers**: None found in current codebase (internal only)

**3. `IDirectConversationRepository`**
*   **Method**: `GetByIdAsync(ConversationId conversationId, int selfIdentityId, CancellationToken ct)`
*   **External Callers**:
    *   `RemotePeerResolver.cs` (line 32): `GetByIdAsync(convId, _activeIdentityContext.Identity.SelfIdentityId.Value, ...)`
    *   **Conversion Required**: `_activeIdentityContext.Identity.SelfIdentityId.Value` (Identity domain `SelfId`) → `ChatSelfId`

**Domain Events (Published to External Subscribers):**

**4. `TextMessagePostedEvent`**
*   **Property**: `uint SenderSelfIdentityId`
*   **External Subscribers**:
    *   `TextMessagePostedHandler.cs` (Percolator.Application) - line 44: compares with `_active.Identity.SelfIdentityId.Value`
    *   `ChatStateUpdateHandlers.cs` (Desktop.Wpf) - line 42: passes to `TriggerReloadForConversation(..., notification.SenderSelfIdentityId, ...)`
    *   **Conversion Required**: Event property remains `uint` (Identity domain `SelfId`), subscribers convert to `ChatSelfId` if needed

**5. `TextMessageReceivedEvent`**
*   **Property**: `uint SelfIdentityId`
*   **External Subscribers**:
    *   `ChatStateUpdateHandlers.cs` (Desktop.Wpf) - line 53: passes to `TriggerReloadForConversation(..., notification.SelfIdentityId, ...)`
    *   **Conversion Required**: Event property remains `uint` (Identity domain `SelfId`), subscribers convert to `ChatSelfId` if needed

**6. `DeliveredReceiptReceivedEvent`**
*   **Property**: `uint SelfIdentityId`
*   **External Subscribers**:
    *   `DeliveredReceiptReceivedEventHandler.cs` (Desktop.Wpf) - does not use `SelfIdentityId` property
    *   **Conversion Required**: Event property remains `uint` (Identity domain `SelfId`), subscribers convert to `ChatSelfId` if needed

**7. `ReadReceiptReceivedEvent`**
*   **Property**: `uint SelfIdentityId`
*   **External Subscribers**: None found in current codebase
*   **Conversion Required**: Event property remains `uint` (Identity domain `SelfId`), subscribers convert to `ChatSelfId` if needed

**8. `EmojiAnnotationReceivedEvent`**
*   **Property**: `uint SelfIdentityId`
*   **External Subscribers**: None found in current codebase
*   **Conversion Required**: Event property remains `uint` (Identity domain `SelfId`), subscribers convert to `ChatSelfId` if needed

**Application Commands (Internal to Percolator.Chat):**

**9. `UpdateGroupInfoCommand`**
*   **Property**: `int SelfIdentityId`
*   **External Callers**: None found (internal to Percolator.Chat)
*   **Conversion Required**: Not applicable (internal)

**Implementation Strategy for Chunk 5.4.b:**

When implementing Chunk 5.4, after defining `ChatSelfId`:

1. **Update Repository Interfaces**: Change method signatures from `uint selfIdentityId` to `ChatSelfId selfIdentityId`
2. **Update External Callers**: Convert Identity domain `SelfId` to `ChatSelfId` at call sites:
   - `SendGroupMessageCommandHandler.cs`: `new ChatSelfId(request.SelfIdentityId.Value)`
   - `GroupProvisioningAppService.cs`: `new ChatSelfId(selfIdentityId.Value)`
   - `GroupInviteHandler.cs`: `new ChatSelfId(selfIdentityId.Value)`
   - `PostTextMessageHandler.cs`: `new ChatSelfId(resolution.SelfIdentityId)`
   - `ProcessInternalEnvelopeHandler.cs`: `new ChatSelfId(request.Context.SelfIdentityId.Value)`
   - `RemotePeerResolver.cs`: `new ChatSelfId(_activeIdentityContext.Identity.SelfIdentityId.Value)`
3. **Domain Events**: Keep event properties as `uint` (Identity domain `SelfId`) to avoid breaking existing subscribers. Subscribers can convert to `ChatSelfId` if needed after the refactoring.

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