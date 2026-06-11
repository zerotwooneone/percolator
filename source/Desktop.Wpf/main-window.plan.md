
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

---

## Group Messaging Implementation Plan (V2)

**Design** Group messaging will be implemented very similar to the Signal protocol. However, as this is a peer to peer application the user must choose a single relay to host the group conversation when the group is created. All group members will need to establish a 1:1 session with the relay before they can participate in the group conversation. The relay maintains the group state - but that state is opaque to the relay.

## Chunk 1
Feature Implementation Request: Signal Protocol Chunk 1 (Identity, Profile Keys & Device IDs)
You are to implement Chunk 1 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Establish the local user's primary device identity, generate a 32-byte symmetric profile key, use it to encrypt the local user's Display Name, and transmit it over our existing 1:1 Double Ratchet channel.

Architectural Constraints (CRITICAL):

Direct Service Orchestration: Do NOT use MediatR for this feature. We are using an explicit IProfileOrchestrationService to manage the workflows.

No Shared Kernel: Our domain libraries (Percolator.Identity, Percolator.Cryptography, Percolator.Chat) DO NOT reference each other.

Duplicate Domain Primitives: You must define the required strongly-typed wrappers locally within each domain that needs them.

Anti-Corruption Layer: Percolator.Application acts as the orchestrator. It must translate types across domain boundaries by extracting the raw byte[] from one domain's type and passing it into the other domain's type using the code-generated public static [Type] FromBytesOwned(byte[] bytes) method to ensure zero-allocation transfers.

No Data Migrations: Do not write EF Core migrations. This is a pre-production environment.

Implementation Requirements
1. The Identity Domain (Percolator.Identity)

Define local primitives:

public readonly record struct DeviceId(uint Value);

[ByteArray(length: 32)] public partial record ProfileKeyBytes;

[ByteArray(minLength: 1, maxLength: 1024)] public partial record EncryptedProfileDataBytes;

Define a wrapper record: public record ProfileCiphertextPackage(EncryptedProfileDataBytes Ciphertext, byte[] Nonce, byte[] Tag);

SelfIdentity Entity: >   * Add public DeviceId DeviceId { get; private set; } = new DeviceId(1); (The primary device is always ID 1).

Add public ProfileKeyBytes? CurrentProfileKey { get; private set; }

Add public ProfileCiphertextPackage? CurrentProfileCiphertext { get; private set; }

Add public int ProfileRevision { get; private set; }

Add a state-transition method: public void CommitProfileUpdate(ProfileKeyBytes newKey, ProfileCiphertextPackage payload) which updates properties and increments the revision.

2. The Cryptography Domain (Percolator.Cryptography)

Define local primitives:

[ByteArray(length: 32)] public partial record ProfileKeyBytes;

[ByteArray(length: 12)] public partial record ProfileNonceBytes;

[ByteArray(length: 16)] public partial record ProfileTagBytes;

[ByteArray(minLength: 1, maxLength: 1024)] public partial record EncryptedProfileDataBytes;

Define a wrapper record: public record ProfileEncryptionResult(EncryptedProfileDataBytes Ciphertext, ProfileNonceBytes Nonce, ProfileTagBytes Tag);

IProfileCryptographyService: Implement ProfileEncryptionResult EncryptData(byte[] serializedProfileData, ProfileKeyBytes key) and byte[] DecryptData(EncryptedProfileDataBytes ciphertext, ProfileNonceBytes nonce, ProfileTagBytes tag, ProfileKeyBytes key).

3. Network Contracts & Persistence (Percolator.Infrastructure)

Protobuf (messaging.proto / internal_messaging.proto): >   * Define ProfileData { optional string display_name = 1; }.

Update ChatEnvelope with optional bytes profile_key = 10;, optional bytes encrypted_profile_data = 11;, optional bytes profile_nonce = 12;, optional int32 profile_revision = 13; (and profile_tag if needed).

SelfIdentityDbo: >   * Add public uint DeviceId { get; set; } = 1;

Add the necessary byte[] and int columns to persist the ProfileKey, Ciphertext, Nonce, Tag, and Revision.

PeerIdentityDbo: >   * Add public uint PrimaryDeviceId { get; set; } = 1;

Add public byte[]? ProfileKey { get; set; } and public int LastKnownProfileRevision { get; set; }.

4. Application Orchestration (Percolator.Application)

Create IProfileOrchestrationService with three methods:

Task UpdateLocalProfileAsync(string newDisplayName, CancellationToken ct)

Task AttachProfileDataIfRequiredAsync(ChatEnvelope envelope, Guid recipientPeerId, CancellationToken ct)

Task ProcessInboundProfileDataAsync(ChatEnvelope envelope, Guid senderPeerId, CancellationToken ct)

Implement the Service:

UpdateLocalProfile: Load SelfIdentity, generate secure random bytes, wrap in Identity and Cryptography keys via FromBytesOwned, serialize Protobuf, call Crypto service, map result back to Identity package, call CommitProfileUpdate, and save.

Attach (Send Pipeline): Check if local user's ProfileRevision > recipient peer's LastKnownProfileRevision. If yes, map the raw bytes from the local DBO into the Protobuf envelope.

Process (Receive Pipeline): If inbound ChatEnvelope has profile_revision > local PeerIdentityDbo.LastKnownProfileRevision, extract bytes, translate to Cryptography types, decrypt, and update the peer's name, key, and revision in the database.

Integration: Show how MessageService and ProcessInternalEnvelopeHandler inject and call this new service.

## Chunk 2
### Architectural Decisions: Moving from Asynchronous Pre-loading to a Synchronous Factory
The initial design for Chunk 2 relied on an asynchronous pre-loading pattern ("Async Pre-load -> Sync Rust FFI -> Async Flush") using an in-memory dictionary cache to prevent "sync-over-async" thread starvation. However, further architectural review exposed two significant liabilities that made that approach untenable for a production-grade implementation:

The Black-Box Predictability Problem: The unmanaged Rust libsignal FFI acts as a cryptographic black box. When decrypting a complex stream of group messages—potentially arriving out of order or containing interleaved keys—the library may traverse and request historical sender keys that the application layer cannot reliably predict. If the application layer guesses wrong during the PreLoadAsync phase, the dictionary cache will suffer a miss, the unmanaged layer will receive a "Not Found" error, and message decryption will catastrophically and permanently fail. The interop store must be a direct portal to the absolute source of truth, capable of resolving any key dynamically.

The SQLite I/O Reality:
The fear of sync-over-async deadlocks is a critical constraint when dealing with network-bound database providers (e.g., SQL Server, PostgreSQL) where threads are forced to block while waiting for network round-trips. However, Percolator utilizes SQLite, an in-process, local file-based database. In SQLite, asynchronous I/O operations are largely a managed illusion; synchronous lookups and database writes execute in fractions of a millisecond directly within the calling thread's memory space. Introducing an elaborate state-tracking asynchronous cache layer to avoid blocking on a local file read represents severe over-engineering.

Transaction Isolation via IDbContextFactory:
By utilizing a dedicated IDbContextFactory<PercolatorDbContext>, we can spin up short-lived, transient, synchronous database contexts completely isolated from the ambient request-scoped DbContext. This ensures that if an unmanaged cryptographic operation mutates and saves a Signal ratchet state, those changes are immediately committed to the database. This isolation is mandatory: if the overarching application-layer message delivery transaction fails or rolls back, the cryptographic ratchet states must still be saved to prevent the local client from desynchronizing from the network.

### Feature Implementation Request: Signal Protocol Chunk 2 (FFI Direct Interop Bridge via DbContextFactory)
You are to implement Chunk 2 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict Modular Monolith architecture.

The Goal: Build a safe, memory-pinned, synchronous interop bridge (SenderKeyInteropBridge) that allows the unmanaged synchronous Rust FFI (Signal.Interop) to perform direct database lookups and writes against our SQLite database via an IDbContextFactory<PercolatorDbContext>.

Architectural Constraints (CRITICAL):

No Asynchronous Pre-loading: Do not implement an in-memory predictive cache. The unmanaged callbacks must directly query the database synchronously to ensure reliability.

Isolated Transactions: Use IDbContextFactory<PercolatorDbContext> to instantiate short-lived, isolated DbContext instances within the callbacks. Call .SaveChanges() synchronously inside the storage callbacks.

Zero Memory Leaks: Properly pin the C# delegates using GCHandle.Alloc to prevent the Garbage Collector from sweeping them while unmanaged code holds the VTable. Allocate unmanaged memory using Marshal.AllocHGlobal when returning data to Rust.

Shared Nothing: Entity Framework models and context implementations belong in Percolator.Infrastructure. Clean interfaces belong in Percolator.Cryptography.

Implementation Requirements
1. Persistence (Percolator.Infrastructure)

Create a new database object: SenderKeyRecordDbo.

Configure a composite primary key consisting of: ConversationId (GUID), PeerId (GUID), and DeviceId (uint). (Note: Signal's DistributionId maps to our ConversationId. ACI maps to our PeerId. DeviceId was established in Chunk 1).

Add a public byte[] RecordBytes { get; set; } property to store the serialized blob of the Sender Key.

Register this configuration in PercolatorDbContext. Do not write EF Core migrations.

2. The Interop Bridge Interface (Percolator.Cryptography)

Define public interface ISenderKeyInteropBridge : IDisposable;

Add a method: IntPtr GetVTablePtr();

3. The Bridge Implementation (Percolator.Infrastructure)

Implement SenderKeyInteropBridge implementing ISenderKeyInteropBridge.

Inject IDbContextFactory<PercolatorDbContext> into its constructor.

Memory Pinning:

Declare class-level fields for LoadSenderKeyDelegate and StoreSenderKeyDelegate to preserve their references.

Inside the constructor, instantiate the delegates pointing to your private callback methods and pin them using GCHandle.Alloc(..., GCHandleType.Normal).

Allocate and pin an instance of the SenderKeyStoreVTable structure, populating its function pointers with the pinned delegate addresses.

LoadSenderKey Callback (Synchronous Execution):

Extract ConversationId from the 16-byte distributionIdBytes pointer.

Extract PeerId (ACI) and DeviceId from the opaque senderAddress pointer using Signal.Interop extraction helpers or direct Marshal pointer manipulation.

Use the injected factory to resolve a temporary context: using var db = _dbFactory.CreateDbContext();

Synchronously find the record matching the composite key using db.SenderKeyRecords.Find(...).

If found: Allocate unmanaged memory via Marshal.AllocHGlobal(record.RecordBytes.Length), copy the managed bytes to that address via Marshal.Copy, set the outRecord and outLen parameters, and return 0. (The unmanaged layer will assume ownership and free this memory).

If not found: Set output parameters to zero or null and return 1 (Not Found).

StoreSenderKey Callback (Synchronous Execution):

Extract the composite identifiers (ConversationId, PeerId, DeviceId) as described above.

Copy the incoming unmanaged data from recordBytes and recordLen into a new managed byte[].

Use the factory to resolve a temporary context: using var db = _dbFactory.CreateDbContext();

Perform a synchronous upsert. If the record exists, update its RecordBytes; if it does not exist, add a new SenderKeyRecordDbo instance.

Execute db.SaveChanges(); to immediately commit the state transition to SQLite. Return 0.

Dispose:

Free all allocated GCHandle instances cleanly to avoid memory leaks.

## Chunk 3
Feature Implementation Request: Signal Protocol Chunk 3 (Micro-PKI & Sealed Sender)
You are to implement Chunk 3 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Establish a Micro-PKI for "Sealed Sender". The node acting as a Relay must securely generate and store an Ed25519 Root Key. Clients must securely authenticate over a standard TLS connection using cryptographic header signatures to request a short-lived DeliveryCertificate.

Architectural Constraints (CRITICAL):

Interop is Ready: The Signal.Interop library has already been updated with GenerateEd25519KeyPair, Ed25519Sign, and Ed25519Verify. You just need to wrap them in the Cryptography domain.

Strict Clean Architecture: Infrastructure components (Interceptors, HostedServices) must be "dumb". All business logic, validation rules, and network orchestration must live in Percolator.Application.

No Shared Kernel: Duplicate the [ByteArray] domain primitives locally in the domains that need them.

Anti-Corruption Layer: Percolator.Application orchestrates the translation across boundaries using the zero-allocation public static [Type] FromBytesOwned(byte[] bytes) pattern.

No Data Migrations: Do not write EF Core migrations.

Implementation Requirements
1. The Cryptography Domain (Percolator.Cryptography)

Define strongly-typed primitives:

[ByteArray(length: 32)] public partial record RelayRootKeyBytes;

[ByteArray(length: 32)] public partial record Ed25519PublicKeyBytes;

[ByteArray(length: 64)] public partial record Ed25519SignatureBytes;

Define IEd25519CryptographyService and implement it by wrapping the native Signal.Interop methods, ensuring type safety with the new primitives.

2. Identity Domain & Persistence (Percolator.Identity & Infrastructure)

Primitives: Define matching RelayRootKeyBytes inside Percolator.Identity.

Domain Entity: Update the SelfIdentity aggregate root to include public RelayRootKeyBytes? RelayDeliveryRootKey { get; private set; }. Add a method EnableRelayMode(RelayRootKeyBytes rootKey) to govern this state transition.

Persistence: Add public byte[]? RelayDeliveryRootKey { get; set; } to SelfIdentityDbo.

3. Application-Layer Authentication (The Server Auth Flow)

The Application Logic (Percolator.Application):

Create IPeerAuthenticationService with method: Task<bool> AuthenticateDeliveryCertificateRequestAsync(Guid peerId, DateTimeOffset requestTimestamp, byte[] signature, CancellationToken ct).

Implementation: Reject if requestTimestamp is older than 60 seconds (Replay attack prevention). Lookup the peer's public ECDsa Identity Key from PeerIdentityDbo. Verify the signature using the existing ISigningService. Return true if valid.

The Interceptor (Percolator.Infrastructure):

Create DeliveryCertificateAuthInterceptor : Interceptor.

Extract PeerId, Timestamp, and Signature from the gRPC request metadata.

Call IPeerAuthenticationService.

If it returns false, throw RpcException(StatusCode.Unauthenticated).

4. Relay gRPC Service (Percolator.Infrastructure & Contracts)

Protobuf (messaging.proto):

Define a DeliveryCertificate message containing certificate_data (bytes) and signature (bytes).

Add rpc GetDeliveryCertificate(GetDeliveryCertificateRequest) returns (GetDeliveryCertificateResponse); to TransportService.

Implementation (PercolatorMessageService):

Implement the endpoint. Read the RelayDeliveryRootKey from the local node's SelfIdentityDbo.

Construct the certificate payload bytes (containing the Relay's ID and a 24-hour expiration).

Use IEd25519CryptographyService to sign the payload. Return the response.

5. The Client Certificate Flow (The Rich Domain & Worker)

The Domain Concept (Percolator.Network or appropriate domain):

Define a rich domain record: public record DeliveryCertificate(byte[] SerializedPayload, DateTimeOffset ExpiresAt);

Define an interface IDeliveryCertificateStore to hold this singleton in memory.

The Orchestrator (Percolator.Application):

Create ICertificateOrchestrator with Task RefreshLocalCertificateAsync(CancellationToken ct).

Implementation: Generate current UTC timestamp. Ask Identity domain to sign [PeerId + Timestamp] using the local ECDsa Identity Key. Call the Relay's GetDeliveryCertificate gRPC endpoint. Parse the response into the rich DeliveryCertificate record (extracting the expiration date), and save it to IDeliveryCertificateStore.

The Background Worker (Percolator.Infrastructure):

Implement DeliveryCertificateRefreshWorker : IHostedService.

Logic: Hook into IHostApplicationLifetime.ApplicationStarted. Create a dumb while (!ct.IsCancellationRequested) loop. Inside the loop, create an AsyncServiceScope, resolve ICertificateOrchestrator, and call RefreshLocalCertificateAsync(). Then await Task.Delay(TimeSpan.FromHours(20), ct); to trigger well before the 24-hour expiration.

## Chunk 4
Feature Implementation Request: Signal Protocol Chunk 4 (Relay Encrypted Ledger)
You are to implement Chunk 4 of our Signal Protocol Group V2 integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Transform the Relay into an authoritative, encrypted ledger for group state. The Relay must enforce Optimistic Concurrency (Epochs), verify Zero-Knowledge (ZK) Proofs before accepting messages, and synchronously fan-out payloads to group members.

Architectural Constraints (CRITICAL):

Interop is Ready: The Signal.Interop library already contains VerifyAuthCredentialWithPniPresentation and all necessary ZK SafeHandle wrappers. Wrap these in the Cryptography domain.

Strict Clean Architecture: The Application layer must NOT reference Entity Framework, DbContext, or DBOs. Repositories must catch EF Core exceptions (like DbUpdateConcurrencyException) and translate them into pure Domain exceptions before they reach the Application layer.

Rich Domain Model: State transitions (like advancing an epoch) must happen on a Domain entity, not in a service method.

Synchronous Fan-Out: The infrastructure implementation of the queue repository must execute a synchronous bulk-insert for all fanned-out messages within a single transaction.

No Data Migrations: Do not write EF Core migrations.

Implementation Requirements
1. The Domain Layer (Percolator.Network or appropriate domain)

Entity: Create a RelayGroupLedger aggregate root.

Properties: GroupId, CurrentEpoch.

Behavior: public void AdvanceEpoch(uint requestedEpoch). This method must throw a StaleEpochDomainException(uint CurrentEpoch) if the requested epoch is less than or equal to CurrentEpoch. If valid, it updates CurrentEpoch.

2. The Cryptography Domain (Percolator.Cryptography)

Define strongly-typed primitives: [ByteArray] public partial record ZkPresentationBytes, ZkServerSecretParamsBytes, and ZkGroupPublicParamsBytes.

Define IZkGroupCryptographyService with: bool VerifyGroupPresentation(ZkPresentationBytes presentation, ZkServerSecretParamsBytes serverSecret, ZkGroupPublicParamsBytes groupPublic, ulong redemptionTime).

Implement the service wrapping the existing SignalCrypto.VerifyAuthCredentialWithPniPresentation method.

3. Identity & Relay Persistence (Percolator.Identity & Infrastructure)

Primitives: Add ZkServerSecretParamsBytes primitive to Percolator.Identity.

Domain Entity: Update SelfIdentity to include ZkServerSecretParamsBytes. Generate it alongside the RelayDeliveryRootKey during EnableRelayMode().

Persistence: Add public byte[]? ZkServerSecretParams { get; set; } to SelfIdentityDbo.

Ledger Persistence: Create RelayGroupStateDbo (GroupId PK, Epoch, and an explicit int Version for EF Core concurrency token).

4. Network Contracts (messaging.proto & Contracts)

Define the Protobuf response:

Protocol Buffers
message PublishGroupMessageResponse {
enum Status {
SUCCESS = 0;
EPOCH_CONFLICT = 1;
UNAUTHORIZED = 2;
}
Status status = 1;
optional uint32 current_relay_epoch = 2;
}
Define PublishGroupMessageRequest containing group_id, ciphertext, epoch, zk_auth_presentation, and redemption_time.

Add rpc PublishGroupMessage(PublishGroupMessageRequest) returns (PublishGroupMessageResponse); to TransportService.

5. Infrastructure Repositories (Percolator.Infrastructure)

IRelayGroupRepository: Implement Task<RelayGroupLedger> GetLedgerAsync(Guid groupId) and Task SaveAsync(RelayGroupLedger ledger).

Critical Rule: Inside SaveAsync, wrap await _dbContext.SaveChangesAsync() in a try/catch. If DbUpdateConcurrencyException is caught, reload the RelayGroupStateDbo from the database and throw a pure EpochConflictDomainException(uint winningEpoch).

IMessageQueueRepository: Add Task EnqueueFanOutAsync(Guid groupId, byte[] payload, CancellationToken ct).

Implementation: Resolve the list of registered PeerIds for the group. Generate a MessageQueueItemDbo for each peer. Use _dbContext.MessageQueueItems.AddRange() to perform a synchronous bulk-insert.

6. Application Orchestration (Percolator.Application)

Create IRelayGroupLedgerService with Task<PublishGroupMessageResponse> PublishAsync(...).

The Flow:

Authenticate: Use IZkGroupCryptographyService to verify the presentation against the Relay's secret and the Group's public params. Return Status.UNAUTHORIZED if false.

Load: Call IRelayGroupRepository.GetLedgerAsync().

Mutate: Call ledger.AdvanceEpoch(request.Epoch). (Catch StaleEpochDomainException and return Status.EPOCH_CONFLICT with the current epoch).

Queue: Call IMessageQueueRepository.EnqueueFanOutAsync(...).

Commit: Call IRelayGroupRepository.SaveAsync(ledger). (Catch EpochConflictDomainException and return Status.EPOCH_CONFLICT with the winning epoch).

Return Status.SUCCESS.

7. Relay gRPC Service (Percolator.Infrastructure)

Implement the PublishGroupMessage endpoint. Map the request, call IRelayGroupLedgerService.PublishAsync(), and map the result back to Protobuf. Keep the gRPC layer dumb.

## Chunk 5
Feature Implementation Request: Signal Protocol Chunk 5 (Group Provisioning via Outbox)
You are to implement Group Provisioning using an Outbox pattern. This ensures that group creation and the subsequent invitations are atomic and resilient to network failures.

The Goal: Alice creates a group, persists the state atomically, and creates an outbox message. A background worker dispatches the invite over 1:1 encrypted tunnels.

Architectural Constraints (CRITICAL):

Atomic Outbox: Use an Outbox pattern. The Group state and the OutboxMessage must be committed in a single EF Core transaction.

Domain Aggregates: Logic for membership and state transitions must live in a GroupConversation aggregate root.

Infrastructure-Agnostic Application: The Application layer must use interfaces for message delivery and repository access. It must NOT reference Protobufs or EF Core DBOs.

Implementation Requirements
1. Domain Layer (Percolator.Chat / Percolator.Domain)

GroupConversation Aggregate: >   * Properties: GroupId, MasterKey, Epoch, List<GroupMember> Members.

Methods: InviteMember(PeerId peer) which updates state and registers a MemberInvitedDomainEvent.

IDomainEvent / OutboxMessage: Define a record for MemberInvitedDomainEvent containing GroupId, PeerId, and the pre-generated SenderKeyDistributionMessage.

2. Infrastructure Layer (Percolator.Infrastructure)

RelayOutboxDbo: Create a DBO to store pending domain events: Id, EventType, PayloadJson, ProcessedAtUtc.

OutboxDispatcherWorker: An IHostedService that runs periodically. It queries the Outbox table for unprocessed events, resolves the domain event, calls the IMessageService to dispatch the invite, and marks the event as processed.

3. Application Orchestration (Percolator.Application)

CreateGroupCommandHandler:

Generate GroupMasterKey via IGroupCryptographyService.

Instantiate GroupConversation domain entity.

Call group.InviteMember(peerId) for each initial member.

Use a IUnitOfWork to save the GroupConversation and the resulting MemberInvitedDomainEvents into the Outbox table within a single transaction.

4. Provisioning & Invite Logic

IGroupSessionBuilder: Add byte[] BuildDistributionMessage(GroupMasterKey masterKey, PeerId recipientId).

ProcessInviteHandler: Add a new case to ProcessInternalEnvelopeHandler for GroupInvite.

Persist the GroupMasterKey into GroupCryptoStateDbo.

Import the DistributionMessage via the VTable bridge.

Update PendingGroupInvitationDbo status to Accepted.

5. Network Contracts (internal_messaging.proto)

Add GroupInvite message:

Protocol Buffers
message GroupInvite {
optional uint32 version = 1;
optional bytes conversation_id = 2;
optional string group_name = 3;
optional bytes group_master_key = 4; // 32 bytes
optional bytes sender_key_distribution_message = 5;
}
Add group_invite = 16; to the ChatEnvelope oneof.

Please output the C# code for the GroupConversation aggregate root, the OutboxMessage DBO and dispatcher, the CreateGroupCommandHandler, and the ProcessInvite ingress logic.

## Chunk 6
Feature Implementation Request: Signal Protocol Chunk 6 (The Data Plane)
You are to implement the high-velocity Data Plane for Group V2.

The Goal: Build an asynchronous, decoupled pipeline for group message ingress and fan-out.

Architectural Constraints (CRITICAL):

Interface Segregation: The Application layer must define IGroupNotificationDispatcher. The Infrastructure layer implements this interface using gRPC streams. The Application layer must never see an IServerStreamWriter.

Decoupled Concurrency: Do not lock the whole group during decryption. Parallelize decryption (CPU-bound). Only serialize persistence/database writes (I/O-bound).

No Infrastructure Leaks: The Application layer handles the business logic; the Infrastructure layer handles the gRPC streaming and database locking.

Implementation Requirements
1. Interface Definition (Percolator.Application)

public interface IGroupNotificationDispatcher { Task DispatchAsync(Guid groupId, MessageDto message, CancellationToken ct); }

2. Infrastructure Implementation (Percolator.Infrastructure)

GrpcGroupNotificationDispatcher: Implements IGroupNotificationDispatcher.

Holds the ConcurrentDictionary<Guid, IServerStreamWriter<GroupStreamResponse>>.

Implements the StreamGroupMessages gRPC method.

When DispatchAsync is called, it iterates the connected streams and pushes the message.

SqliteChatMessageWriter: Wrap the persistence logic in a SemaphoreSlim or a single-threaded task queue to ensure database writes are serialized and atomic.

3. Application Orchestration (Percolator.Application)

GroupIngressService.ProcessGroupMessageAsync:

Decrypt: Call ISenderKeyCryptographyService.Decrypt(...) (No lock required).

Persist: Call IChatMessageWriter.AddGroupMessageAsync(...). (The Infrastructure handles locking).

Fan-Out: Call IGroupNotificationDispatcher.DispatchAsync(...).

4. gRPC Streaming Service

Implement the StreamGroupMessages method in PercolatorMessageService.

It should register the stream with GrpcGroupNotificationDispatcher on connect.

It should keep the stream open using a while (!context.CancellationToken.IsCancellationRequested) loop.

It should remove the stream on disconnect.

Please output the C# code for the IGroupNotificationDispatcher interface, the GrpcGroupNotificationDispatcher infrastructure implementation, and the updated PercolatorMessageService streaming logic.

## Chunk 7
Feature Implementation Request: Signal Protocol Chunk 7 (Client-Side Speculative Rebase Coordinator)
You are to implement Chunk 7 of our Signal Protocol Group V2 integration for Percolator, isolating client-side conflict resolution behind a reusable Process Manager.

Architectural Constraints (CRITICAL):

No Dirty Memory States: Do not apply state mutations directly to tracked repository entities before network confirmation. Speculative mutations must be verified cleanly without dirtying live cache entities.

Reusable Coordination: Do not write retry loops or network synchronization code inside individual MediatR handlers. Centralize this orchestration within an application-layer Process Manager (GroupMutationCoordinator).

Intent-Based Validation: Group mutations must be modeled as structural proposals so they can be re-evaluated for validity if the group baseline shifts during a sync catch-up.

Implementation Requirements
1. The Proposal Model & Domain Safeguard (Percolator.Chat)

Define an interface for mutations: IGroupMutationProposal.

Implement an explicit proposal record, e.g., RenameGroupProposal(string NewName) : IGroupMutationProposal.

Update the GroupConversation aggregate to support deep copying or dry validation:

public bool EvaluateProposal(IGroupMutationProposal proposal, out string? businessRuleViolation)

2. The Mutation Coordinator Process Manager (Percolator.Application)

Create a centralized service: GroupMutationCoordinator.

Method Signature: Task<MutationResult> CoordinateMutationAsync(Guid groupId, IGroupMutationProposal proposal, int selfIdentityId, CancellationToken ct)

The Core Loop Engine:

Establish a strict retry limit loop (maximum 3 attempts).

Step 1: Load a completely fresh instance of the aggregate from IGroupConversationRepository.

Step 2: Execute group.EvaluateProposal(proposal, out var error). If it fails validation due to a state change found during catch-up, abort instantly and return MutationResult.Failed(error).

Step 3: Serialize the proposal intent to an encrypted payload using the current aggregate epoch context.

Step 4: Dispatch to IRelayClient.PublishGroupMutationAsync(...).

Step 5 (On Success): Now that consensus is won, apply the mutation directly to the domain object, commit it locally using _repository.UpdateAsync(...), and return success.

Step 6 (On Conflict): Call _relayClient.FetchMissingEpochsAsync(...). Pass the returned delta payload directly to IGroupSyncService.FastForwardLocalStateAsync(...) to advance the baseline SQLite database. Yield thread execution to the next iteration loop.

3. Refactored Application Handlers (Percolator.Application)

Refactor UpdateGroupInfoHandler to be completely lean. It should simply instantiate a RenameGroupProposal, pass it directly to the GroupMutationCoordinator, and evaluate the returned structural outcome.

## Chunk 8
Feature Implementation Request: Signal Protocol Chunk 8 (P2P Relay Opt-In & R3 State Engine)
You are to implement Chunk 8 of our Signal Protocol integration for Percolator, allowing client nodes to dynamically opt-in to hosting a blind group relay and managing the network state via an R3-powered WPF state service.

Architectural Constraints (CRITICAL):

R3 State Alignment: Do NOT leak server lifecycle tracking or state into the Domain. Implement an Angular-style RelayStateService using R3's ReactiveProperty<T> and the DisposableBag cleanup pattern.

WPF ViewModel Isolation: ViewModels must remain completely isolated from Kestrel or network managers. They should purely bind to the RelayStateService via BindableReactiveProperty<T>.

Encrypted Local Persistence: Create a dedicated table in your encrypted SQLite schema to map a GroupId directly to its designated RelayPeerId.

Simple Capability Discovery: Keep capability discovery simple for this phase. If a peer has historically acted as a relay for us in a group context, assume they maintain that capability. Handle connection failures gracefully at runtime.

Implementation Requirements
1. Encrypted Persistence Layer (Percolator.Infrastructure & Chat)

The Schema: Create a GroupRelayMappingDbo.

Properties: Guid GroupId (PK), Guid RelayPeerId, DateTimeOffset LastAssignedUtc.

The Repository: Create IGroupRelayMappingRepository in the Percolator.Chat domain and implement its SQLite backing in Percolator.Infrastructure. It must support basic Upsert and GetRelayForGroupAsync queries.

2. The Presentation State Plane (Desktop.Wpf)

The State Service: Create RelayStateService as an Angular-style application singleton.

C#
public sealed class RelayStateService : IDisposable
{
private readonly ReactiveProperty<bool> _isRelayRunning = new(false);
private readonly Subject<bool> _toggleSubject = new();
private readonly DisposableBag _bag = new();

    public ReadOnlyReactiveProperty<bool> IsRelayRunning => _isRelayRunning;

    public RelayStateService(IMediator mediator, TimeProvider timeProvider)
    {
        _isRelayRunning.AddTo(ref _bag);
        _toggleSubject.AddTo(ref _bag);

        // Batch / Debounce rapid user toggles using Chunk to prevent thrashing Kestrel
        _toggleSubject
            .Chunk(TimeSpan.FromMilliseconds(300), timeProvider)
            .Where(toggles => toggles.Length > 0)
            .SubscribeAwait(async (toggles, ct) => 
            {
                bool finalIntent = toggles[^1]; // Execute the latest user intent
                await mediator.Send(new ToggleRelayHostingCommand(finalIntent), ct);
            }, AwaitOperation.Sequential)
            .AddTo(ref _bag);
    }

    public void RequestToggle(bool enable) => _toggleSubject.OnNext(enable);
    public void UpdateRunningState(bool running) => _isRelayRunning.Value = running;
    public void Dispose() => _bag.Dispose();
}
The DI Registration: Register RelayStateService as a Singleton in App.xaml.cs.

The ViewModel: Update ShellViewModel to inject RelayStateService. Expose:

public BindableReactiveProperty<bool> IsRelayEnabled { get; }

public AsyncRelayCommand ToggleRelayCommand { get; }

Bind IsRelayEnabled directly to the state service property using .ToBindableReactiveProperty().

3. Application Orchestration (Percolator.Application)

Implement the MediatR handler for ToggleRelayHostingCommand(bool Enable).

Logic:

Inject the infrastructure IGrpcServerManager.

If Enable == true: Load local self-identity configurations, and invoke await _serverManager.StartAsync(selfId, port, ct). If successful, call _relayStateService.UpdateRunningState(true).

If Enable == false: Invoke await _serverManager.StopAsync(ct) to cleanly dismantle the Kestrel application host instance and free the network port. Update the state service running state to false.

## Chunk 9
Feature Implementation Request: Signal Protocol Chunk 9 (WPF MVVM Presentation Layer)
You are to implement Chunk 9 of our Signal Protocol Group V2 integration for Percolator, surfacing Group Creation, Invitation Management, and Relay Host Controls.

Architectural Constraints (CRITICAL):

Zero Infrastructure in UI: ViewModels must NEVER reference DBOs. They must bind strictly to Application-provided Read Models.

Direct Application Services: UI actions must be executed via direct calls to Application layer services (e.g., IGroupInvitationAppService), avoiding unnecessary MediatR boilerplate.

Nested Reactivity: Use R3 ReactiveProperty<T> inside the presentation models to allow surgical UI updates without list thrashing.

Implementation Requirements
1. Multi-Select Roster & Group Creation Dialog (Desktop.Wpf)

The Wrapper Model: Create SelectablePeerItemViewModel. It wraps PeerConnectionModel and adds a BindableReactiveProperty<bool> IsSelected.

The Dialog ViewModel: Create CreateGroupDialogViewModel.

Properties: BindableReactiveProperty<string> GroupName, ObservableList<SelectablePeerItemViewModel> SelectablePeers.

Commands: AsyncRelayCommand ConfirmCreateCommand.

Behavior: On execution, filter out selected peers, extract their identifiers, and invoke a direct call to the Application layer to create the group. Close the window upon completion via IWindowManager logic.

The View Configuration: Map CreateGroupDialogWindow.xaml to the ViewModel in ViewMappings.xaml.

2. Group Invitation Management UI (Desktop.Wpf & Percolator.Application)

The Reactive Model: Define PendingInviteModel in the Application layer. It must use ReactiveProperty<T> for mutable state like InviteStatus.

The App Service: Create IGroupInvitationAppService with AcceptAsync(Guid inviteId) and IgnoreAsync(Guid inviteId).

The Menu ViewModel: Create GroupInvitesMenuViewModel.

Project an ISynchronizedView from the State Service's observable list of PendingInviteModels.

The Interaction Actions: Expose two commands:

AcceptInviteCommand(Guid inviteId): Calls await _inviteAppService.AcceptAsync(inviteId).

IgnoreInviteCommand(Guid inviteId): Calls await _inviteAppService.IgnoreAsync(inviteId).

The Badge Bridge: Bind the count of the synchronized list to the custom MatButton.NotificationCount indicator on the sidebar.

3. Relay Host Settings Panel (Desktop.Wpf)

Inject the singleton RelayStateService into the relevant Settings ViewModel.

Declare: public BindableReactiveProperty<bool> HostRelaySwitch { get; }

Bind it directly to the state engine using .ToBindableReactiveProperty().

The View Binding: Render a MatSlideToggle control in XAML:

Code snippet
<controls:MatSlideToggle IsChecked="{Binding HostRelaySwitch.Value, Mode=TwoWay}" />
Ensure toggling the layout passes the boolean state to _relayStateService.RequestToggle(value).

## Chunk 10
Sticking with the lightweight mock approach is the pragmatic call. It keeps the simulator blazing fast, memory-efficient, and free from the overhead of spinning up entire DI scopes and database providers for every mock peer. We accept the small dual-maintenance burden on the protocol scripting in exchange for a highly performant, standalone test harness.

By introducing the ISimulatedGroupOrchestrator, we completely shield your WPF ViewModels from the cryptography layer, preserving your Clean Architecture boundaries.

Here is the finalized, boundary-safe prompt for Chunk 10.

The Prompt for Your Implementation AI
Copy and paste the text below:

Feature Implementation Request: Signal Protocol Chunk 10 (The Lightweight Group Simulator)
You are to implement Chunk 10 of our Signal Protocol Group V2 integration for Percolator. This chunk extends the existing in-memory simulator to test Group Creation, Invites, and Messaging against the Main Application, strictly avoiding Smart UI anti-patterns.

Architectural Constraints (CRITICAL):

Smart UI Prevention: ViewModels must NOT contain FFI logic, Protobuf serialization, or key generation. They must delegate entirely to an Application-layer orchestrator.

State/Behavior Separation: SimulatedPeerModel must remain a lightweight, state-only mock object. Do not embed protocol execution logic inside the model itself.

Direct Interception: Leverage the existing SimulatorOutboundInterceptor and SimulatorToMainTransportService to pass ChatEnvelopes back and forth.

Implementation Requirements
1. Simulated Group Crypto State (Desktop.Wpf/Features/Simulator)

Extend the existing SimulatedPeerModel:

Add public Dictionary<Guid, byte[]> GroupMasterKeysMutable { get; } = new();

2. The Simulator Orchestrator (Desktop.Wpf/Features/Simulator)

Create ISimulatedGroupOrchestrator and its implementation. This is the engine that drives the mock protocol.

Methods:

Task HandleIngressAsync(SimulatedPeerModel peer, ChatEnvelope envelope):

If it's a GroupInvite, extract the GroupMasterKey, save it to the peer's GroupMasterKeysMutable, and process the SenderKeyDistributionMessage via SignalCrypto.

If it's a GroupMessage, decrypt it via the FFI layer and log it to the simulator's diagnostic output.

Task CreateGroupWithMainAsync(SimulatedPeerModel initiator):

Generate a GroupMasterKey, create the distribution message, package it into a GroupInvite Protobuf, and dispatch via SimulatorToMainTransportService.

Task SendGroupMessageAsync(SimulatedPeerModel sender, Guid groupId, string message):

Encrypt the string using the mock peer's SenderKey, package the Protobuf, and dispatch via the transport service.

3. Simulator Ingress Wiring (Main -> Simulator)

Update SimulatorStateService.ReceiveOpaqueMessageFromMainAsync.

After decrypting an incoming 1:1 ChatEnvelope, check if it contains Group V2 payloads (Invites or Group Messages). If so, immediately hand it off to await _groupOrchestrator.HandleIngressAsync(peer, envelope).

4. Simulator Egress & Presentation (Desktop.Wpf/Features/Simulator)

Update SimulatedPeerCardViewModel to include two new, clean macro commands:

AsyncRelayCommand CreateGroupWithMainCommand: Calls await _groupOrchestrator.CreateGroupWithMainAsync(_peerModel).

AsyncRelayCommand SendGroupMessageCommand: Calls await _groupOrchestrator.SendGroupMessageAsync(_peerModel, selectedGroupId, testMessage).

Update SimulatedPeerCardView.xaml to surface these two commands as standard MatButton controls.