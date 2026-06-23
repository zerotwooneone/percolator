
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
Feature Implementation Request: Signal Protocol Chunk 1 (Identity, Profile Keys & Device IDs)
You are to implement Chunk 1 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Establish the local user's primary device identity, generate a 32-byte symmetric profile key, use it to encrypt the local user's Display Name, and transmit it over our existing 1:1 Double Ratchet channel.

**IMPLEMENTATION NOTES:**
- **Identity Domain:** Implemented `DeviceId`, `ProfileKeyBytes`, `EncryptedProfileDataBytes`, and `ProfileCiphertextPackage` primitives. Added `DeviceId`, `CurrentProfileKey`, `CurrentProfileCiphertext`, and `ProfileRevision` to `SelfIdentity` entity with `CommitProfileUpdate` state transition method.
- **Cryptography Domain:** Implemented local primitives and `IProfileCryptographyService` with `EncryptData` and `DecryptData` methods using AES-GCM.
- **Persistence:** Updated `SelfIdentityDbo` and `PeerIdentityDbo` with profile-related columns. Updated `ChatEnvelope` protobuf with profile fields.
- **Application Orchestration:** Implemented `IProfileOrchestrationService` with `UpdateLocalProfileAsync`, `AttachProfileDataIfRequiredAsync`, and `ProcessInboundProfileDataAsync` methods. Integrated with `MessageService` and `ProcessInternalEnvelopeHandler`.

Architectural Constraints (CRITICAL):

Direct Service Orchestration: Do NOT use MediatR for this feature. We are using an explicit IProfileOrchestrationService to manage the workflows.

No Shared Kernel: Avoid adding any NEW project dependencies between our domain libraries (Percolator.Identity, Percolator.Cryptography, Percolator.Chat).

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

[ByteArray(minLength: 1, maxLength: 1024)] public partial record ProfilePlaintextBytes;

[ByteArray(length: 12)] public partial record ProfileNonceBytes;

[ByteArray(length: 16)] public partial record ProfileTagBytes;

[ByteArray(minLength: 1, maxLength: 1024)] public partial record EncryptedProfileDataBytes;

Define a wrapper record: public record ProfileEncryptionResult(EncryptedProfileDataBytes Ciphertext, ProfileNonceBytes Nonce, ProfileTagBytes Tag);

IProfileCryptographyService: Implement ProfileEncryptionResult EncryptData(ProfilePlaintextBytes serializedProfileData, ProfileKeyBytes key) and ProfilePlaintextBytes DecryptData(EncryptedProfileDataBytes ciphertext, ProfileNonceBytes nonce, ProfileTagBytes tag, ProfileKeyBytes key).

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

Task AttachProfileDataIfRequiredAsync(ChatEnvelope envelope, PeerId recipientPeerId, CancellationToken ct)

Task ProcessInboundProfileDataAsync(ChatEnvelope envelope, PeerId senderPeerId, CancellationToken ct)

Implement the Service:

UpdateLocalProfile: Load SelfIdentity, generate secure random bytes, wrap in Identity and Cryptography keys via FromBytesOwned, serialize Protobuf, call Crypto service, map result back to Identity package, call CommitProfileUpdate, and save.

Attach (Send Pipeline): Check if local user's ProfileRevision > recipient peer's LastKnownProfileRevision. If yes, map the raw bytes from the local DBO into the Protobuf envelope.

Process (Receive Pipeline): If inbound ChatEnvelope has profile_revision > local PeerIdentityDbo.LastKnownProfileRevision, extract bytes, translate to Cryptography types, decrypt, and update the peer's name, key, and revision in the database.

Integration: Show how MessageService and ProcessInternalEnvelopeHandler inject and call this new service.

**Testing Requirements (Chunk 1):**
- `SelfIdentity_CommitProfileUpdate_IncrementsRevisionAndUpdatesPayload` - Test that calling CommitProfileUpdate increments ProfileRevision and updates CurrentProfileKey/CurrentProfileCiphertext
- `EncryptedProfileDataBytes_ThrowsException_WhenPayloadExceedsMaxLength` - Test that EncryptedProfileDataBytes.FromBytesOwned throws ArgumentException when payload exceeds maximum length
- `ProfileCryptoRoundTrip_EncryptThenDecrypt_YieldsOriginalPlaintext` - Unit test in Percolator.CryptographyTests that passing a plaintext string through EncryptData and immediately routing the resulting ciphertext, nonce, and tag into DecryptData using the same ProfileKeyBytes yields the original string without data corruption

**Note:** Round-trip integration tests should be implemented in their respective domain unit test projects (e.g., Percolator.CryptographyTests, Percolator.InfrastructureTests), NOT in the ApplicationIntegrationTests project.
---
## Chunk 2 ✅ COMPLETE
### Feature Implementation Request: Signal Protocol Chunk 2 (FFI Direct Interop Bridge via DbContextFactory)
You are to implement Chunk 2 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict Modular Monolith architecture.

The Goal: Build a clean, synchronous interop bridge (`SenderKeyInteropBridge`) that allows the managed cryptographic layer to query and persist unmanaged Sender Key states directly against our SQLite database using an `IDbContextFactory<PercolatorDbContext>`.

**IMPLEMENTATION NOTES:**
- **Cryptography Primitives:** Implemented `DeviceId`, `PeerId`, `ConversationId` primitives in `Percolator.Cryptography.Primitives` and `SenderKeyRecordBytes` in the main namespace.
- **Interop Bridge Contract:** Defined `ISenderKeyInteropBridge` with `TryLoadSenderKey` and `StoreSenderKey` methods using cryptography domain primitives.
- **Persistence:** Created `SenderKeyRecordDbo` with composite primary key (ConversationId, SenderPeerId, DeviceId). Registered `DbSet<SenderKeyRecordDbo>` in `PercolatorDbContext` with explicit fluent mapping.
- **Bridge Implementation:** Implemented `SenderKeyInteropBridge` in `Percolator.Infrastructure.Chat` using `IDbContextFactory<PercolatorDbContext>` for isolated transactions. Implemented synchronous upsert logic for `StoreSenderKey` and composite key lookup for `TryLoadSenderKey`.

Architectural Constraints (CRITICAL):
* **SafeHandle Native Resource Management:** Follow the established codebase pattern utilizing direct static method calls to `Signal.Interop.SignalCrypto` backed by custom `SafeHandle` classes (e.g., `GroupMasterKeySafeHandle`). Do not introduce delegate pinning, `GCHandle`, or raw function pointer VTables.
* **Isolated Transactions:** Use `IDbContextFactory<PercolatorDbContext>` to instantiate short-lived, isolated DbContext instances inside the storage operations. Call `.SaveChanges()` synchronously to ensure cryptographic state transitions commit immediately, preventing desynchronization if an ambient application transaction rolls back.
* **Shared Nothing (Local Primitives):** Entity Framework models and context configurations belong exclusively in `Percolator.Infrastructure`. Clean interfaces live in `Percolator.Cryptography`. To prevent project-level dependencies between Cryptography, Chat, and Identity, you must define necessary strongly-typed primitive wrappers locally within the `Percolator.Cryptography` namespace.

Implementation Requirements
1. Cryptography Local Domain Primitives (`Percolator.Cryptography.Primitives`)
* Define your type invariants locally in the Primitives subnamespace to avoid cross-project coupling:
  ```csharp
  public readonly record struct DeviceId(uint Value);
  public readonly record struct PeerId(Guid Value);
  public readonly record struct ConversationId(Guid Value);
  ```
* Define domain-specific types in the main Cryptography namespace:
  ```csharp
  [ByteArray(minLength: 1, maxLength: 4096)] public partial record SenderKeyRecordBytes;
  ```

2. The Interop Bridge Contract (`Percolator.Cryptography`)
* Define `public interface ISenderKeyInteropBridge`.
* Add `using Percolator.Cryptography.Primitives;` to import the primitive types.
* Expose methods using your newly defined cryptography domain primitives:
  ```csharp
  bool TryLoadSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, out byte[] recordBytes);
  void StoreSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, byte[] recordBytes);
  ```

3. Persistence (`Percolator.Infrastructure/Chat/Persistence`)
* Create a database entity object: `SenderKeyRecordDbo`.
* Properties: `Guid ConversationId { get; set; }`, `Guid SenderPeerId { get; set; }`, `uint DeviceId { get; set; }`, and `byte[] RecordBytes { get; set; }`.
* **DbSet Registration:** Add `public DbSet<SenderKeyRecordDbo> SenderKeyRecords { get; set; }` to `PercolatorDbContext`.
* **Explicit Model Mapping:** Inside your `DbContext` configuration or an internal `IEntityTypeConfiguration<SenderKeyRecordDbo>`, map a composite primary key using EF Core fluent syntax:
  `builder.HasKey(x => new { x.ConversationId, x.SenderPeerId, x.DeviceId });`

4. The Bridge Implementation (`Percolator.Infrastructure.Chat`)
* Implement `SenderKeyInteropBridge` implementing `ISenderKeyInteropBridge` in namespace `Percolator.Infrastructure.Chat`.
* Add `using Percolator.Cryptography.Primitives;` to import the primitive types.
* Inject `IDbContextFactory<PercolatorDbContext>` into its constructor.
* **Namespace Mapping Note:** The infrastructure class maps wrapped domain inputs (`Percolator.Cryptography.Primitives.ConversationId`, `Percolator.Cryptography.Primitives.PeerId`, `Percolator.Cryptography.Primitives.DeviceId`) down to primitive `Guid` and `uint` parameters when invoking the underlying `db.SenderKeyRecords.Find()` composite key query.
* **TryLoadSenderKey Logic:**
    * Resolve a temporary context: `using var db = _dbFactory.CreateDbContext();`.
    * Synchronously locate the record matching the full composite key using `db.SenderKeyRecords.Find(conversationId.Value, senderId.Value, deviceId.Value)`.
    * If found, extract the bytes and return true; if missing, return false.
* **StoreSenderKey Logic:**
    * Resolve a temporary context: `using var db = _dbFactory.CreateDbContext();`.
    * Execute a standard programmatic check-and-update upsert: Read the row via `Find()`. If it exists, overwrite `RecordBytes`. If null, instantiate a new `SenderKeyRecordDbo` with the key coordinates and add it to the tracked set.
    * Execute `db.SaveChanges();` to immediately commit the state transition to SQLite.

**Testing Requirements (Chunk 2):**
- `SenderKeyInteropBridge_TryLoadSenderKey_ReturnsFalse_WhenRecordIsMissing` - Unit test in Percolator.InfrastructureTests that TryLoadSenderKey returns false when the requested record does not exist in the database
- `SenderKeyStatePersistenceRoundTrip_StoreThenLoad_MatchesOriginalBytes` - Integration test in Percolator.InfrastructureTests that a native ratchet state byte array saved via StoreSenderKey using a full composite key (ConversationId, PeerId, uint deviceId) matches the byte array returned by a subsequent TryLoadSenderKey call
---

## Chunk 3 ✅ COMPLETE
Feature Implementation Request: Signal Protocol Chunk 3 (Micro-PKI & Sealed Sender)
You are to implement Chunk 3 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Establish a Micro-PKI for "Sealed Sender". The node acting as a Relay must securely generate and store an Ed25519 Root Key. Clients must securely authenticate over a standard TLS connection using cryptographic header signatures to request a short-lived DeliveryCertificate.

**IMPLEMENTATION NOTES:**
- **Wire Format Abstraction:** Implemented `DeliveryCertificateWireFormatter` to encapsulate the strict 40-byte wire layout (32-byte fingerprint + 8-byte Big-Endian expiration timestamp) with bounds checking and unit tests.
- **Endianness Fix:** Corrected `RelayTransportClient` to use `BinaryPrimitives.ReadInt64BigEndian` instead of `BitConverter.ToInt64` to match the server's Big-Endian encoding.
- **Time Determinism:** Refactored `PeerAuthenticationService` to inject `TimeProvider` for deterministic CI/CD testing. All unit tests now use hard-coded arbitrary timestamps.
- **Aggregate Hydration Fix:** Fixed critical data-loss bug in `SqliteSelfIdentityDomainRepository.Map` to rehydrate `ActiveIdentityKeySpki` from the database.
- **Key Generation Fix:** Fixed `IdentityOrchestrator.ResolveIdentityAsync` to attach newly generated keys to the domain aggregate and persist them, preventing `ActiveIdentityKeyFingerprint` erasure.
- **Unit Test Coverage:** Added comprehensive tests for `DeliveryCertificateWireFormatter`, `PeerAuthenticationService`, and `SelfIdentityDomainRepository` round-trip verification.

**NOT IMPLEMENTED (De-scoped):**
- Ed25519 native interop layer (RelayRootKeyBytes, IEd25519CryptographyService) - deferred to Chunk 4
- SelfIdentity.EnableRelayMode and RelayRootKeyBytes persistence - deferred to Chunk 4
- IRelayTransportClient abstraction - implemented directly in CertificateOrchestrator
- DeliveryCertificateRefreshWorker background service - deferred to Chunk 4
- IDeliveryCertificateStore in-memory singleton - implemented directly in CertificateOrchestrator

Architectural Constraints (CRITICAL):
* **Rule 6 Transport Adherence:** `PeerId` is a local-only database identifier and must NEVER be transmitted over the wire or included in unencrypted gRPC metadata headers. Clients must identify themselves to the relay using their wire-safe Public Key Hash (PKH) string token.
* **Strict Clean Architecture & CQRS:** Infrastructure components (Interceptors, HostedServices, and gRPC endpoints) must be completely decoupled from data access entities. The Application layer must never see or interact with Database Objects (`PeerIdentityDbo`, `SelfIdentityDbo`). For read-only lookups where no state change occurs, use optimized query interfaces to bypass heavy domain repository and change-tracking overhead.
* **Dual-Algorithm Type Invariants:** The Relay Root Key utilizes Ed25519 for issuing standard wire-verifiable certificates. The Client-side Identity challenge utilizes the existing local NIST P-256 ECDsa key pair wrapped inside our established domain primitive types to completely ban raw `byte[]` arrays from application service interfaces.

Implementation Requirements
1. The Cryptography Domain (`Percolator.Cryptography`)
* Ensure your type invariants are verified locally within the cryptography boundary:
  ```csharp
  namespace Percolator.Cryptography;

  [ByteArray(length: 32)] public partial record RelayRootKeyBytes;
  [ByteArray(length: 32)] public partial record Ed25519PublicKeyBytes;
  [ByteArray(length: 64)] public partial record Ed25519SignatureBytes;
  ```
* Define `IEd25519CryptographyService` and implement it by wrapping the native `Signal.Interop` static methods, ensuring type safety with the new cryptography primitives.

2. Identity Domain & Persistence (`Percolator.Identity` & `Percolator.Infrastructure`)
* **Local Identity Primitive:** Define `[ByteArray(length: 32)] public partial record RelayRootKeyBytes;` locally inside the `Percolator.Identity` namespace to avoid cross-project coupling.
* **Domain Entity:** Update the `SelfIdentity` aggregate root to include `public Percolator.Identity.RelayRootKeyBytes? RelayDeliveryRootKey { get; private set; }`. Add a public method `void EnableRelayMode(Percolator.Identity.RelayRootKeyBytes rootKey)` to govern this state transition.
* **Persistence Mapping:** Add `public byte[]? RelayDeliveryRootKey { get; set; }` to `SelfIdentityDbo` inside `Percolator.Infrastructure/Chat/Persistence`.

3. Application-Layer Authentication (The Server Auth Flow)
* The Application Contract & Logic (`Percolator.Application/Chat`):
    * Define application-scoped copies of the discovered primitives to isolate the application library layer:
      ```csharp
      namespace Percolator.Application.Chat;

      [ByteArray(minLength: 64, maxLength: 200)] public partial record RatchetIdentityKey;
      [ByteArray(minLength: 60, maxLength: 120)] public partial record Signature;
      ```
    * Create `IPeerAuthenticationService` exposing the verification contract:
      `Task<bool> AuthenticateDeliveryCertificateRequestAsync(string senderPkh, DateTimeOffset requestTimestamp, Percolator.Application.Chat.Signature signature, CancellationToken ct);`
    * **The Query Interface:** Introduce `public interface IPeerIdentityQueries { Task<Percolator.Application.Chat.RatchetIdentityKey?> GetPublicKeyByPkhAsync(string senderPkh, CancellationToken ct); }` inside `Percolator.Application/Chat` to separate concerns and optimize read performance.
    * **Implementation:** `AuthenticateDeliveryCertificateRequestAsync` rejects immediately if `requestTimestamp` is older than 60 seconds (Replay attack prevention). It then invokes `_peerIdentityQueries.GetPublicKeyByPkhAsync(senderPkh, ct)`. The concrete query implementation inside `Percolator.Infrastructure` maps directly to `PeerIdentityDbo` using a fast, no-tracking (`AsNoTracking()`) SQL projection to pull row bytes and parse them safely using `Percolator.Application.Chat.RatchetIdentityKey.FromBytes()`.
    * **ECDsa Verification Mapping:** The service instantiates an `ECDsa` public key context from the retrieved `RatchetIdentityKey` parameters and verifies the inbound `Signature` payload using `HashAlgorithmName.SHA256`.
* The Interceptor (`Percolator.Infrastructure/Network/Grpc`):
    * Create `DeliveryCertificateAuthInterceptor : Interceptor`.
    * Extract the string token from the `"x-percolator-sender-pkh"` metadata header along with the timestamp and signature bytes. Map the signature bytes using `Percolator.Application.Chat.Signature.FromBytes()`.
    * Call `IPeerAuthenticationService`. If it returns false, throw an `RpcException(StatusCode.Unauthenticated)`.

4. Relay gRPC Service (`Percolator.Infrastructure` & `Percolator.Contracts`)
* Protobuf (`messaging.proto`):
    * Define a `DeliveryCertificate` message containing `certificate_data` (bytes) and `signature` (bytes).
    * Add `rpc GetDeliveryCertificate(GetDeliveryCertificateRequest) returns (GetDeliveryCertificateResponse);` to `TransportService`.
* **The Relay Cryptographic Signing Interface:** Define a secure signature delegation interface in `Percolator.Application/Chat` to isolate key material from the service project layer:
  ```csharp
  namespace Percolator.Application.Chat;

  [ByteArray(length: 64)] public partial record Ed25519SignatureBytes;

  public interface ILocalIdentitySigner
  {
      Task<Percolator.Application.Chat.Ed25519SignatureBytes> SignWithRelayRootKeyAsync(byte[] payload, CancellationToken ct);
      Task<Percolator.Application.Chat.Signature> SignWithLocalIdentityKeyAsync(byte[] payload, CancellationToken ct);
  }
  ```
* **Implementation (`PercolatorMessageService`):**
    * Implement the endpoint. Inject `ILocalIdentitySigner` straight into the gRPC service constructor.
    * Construct the certificate payload bytes (containing the Relay's wire identity fingerprint and a 24-hour expiration counter).
    * **The Secure Sign Call:** Delegate the payload directly to the signer without inspecting raw keys:
      `var signature = await _localIdentitySigner.SignWithRelayRootKeyAsync(certificatePayload, context.CancellationToken);`
    * Package the raw bytes from `signature.ToBytes()` into `DeliveryCertificateResponse` and return.
* **Infrastructure Backing (`Percolator.Infrastructure/Chat`):**
    * Implement `LocalIdentitySigner` implementing `ILocalIdentitySigner`. Inject `IDbContextFactory<PercolatorDbContext>`.
    * `SignWithRelayRootKeyAsync` opens a short-lived `using var db = _dbFactory.CreateDbContext();` context, projects strictly the un-tracked `RelayDeliveryRootKey` byte array from `SelfIdentityDbo`, instantiates `Percolator.Cryptography.RelayRootKeyBytes.FromBytesOwned()`, passes it into the static `Signal.Interop.SignalCrypto` Ed25519 signing engine, and returns the result parsed into `Percolator.Application.Chat.Ed25519SignatureBytes.FromBytesOwned()`.
    * `SignWithLocalIdentityKeyAsync` retrieves the `ECDsa` parameters from the local `SelfIdentityKeysDbo`, invokes standard managed `.SignData()` using `SHA256`, and passes the signature back wrapped inside `Percolator.Application.Chat.Signature.FromBytesOwned()`.

5. The Client Certificate Flow (The Rich Domain & Worker)
* The Domain Concept (`Percolator.Application.Chat`):
    * Define local primitives to isolate the client tracking space:
      `[ByteArray(minLength: 1, maxLength: 2048)] public partial record DeliveryCertificatePayloadBytes;`
    * Define a rich domain record: `public record DeliveryCertificate(DeliveryCertificatePayloadBytes Payload, Percolator.Cryptography.Signature Signature, DateTimeOffset ExpiresAt);`.
    * Define an interface `IDeliveryCertificateStore` to hold this singleton in memory securely using a thread-safe lock pattern.
* **The Application Transport Abstraction (`Percolator.Application/Chat`):**
    * To prevent connection logic from bleeding into core workflows, define an application-layer endpoint gateway:
      ```csharp
      namespace Percolator.Application.Chat;

      public interface IRelayTransportClient
      {
          Task<DeliveryCertificate> FetchCertificateAsync(
              string targetHost, 
              int targetPort, 
              string senderPkh, 
              DateTimeOffset timestamp, 
              Percolator.Cryptography.Signature signature, 
              CancellationToken ct);
      }
      ```
* The Orchestrator (`Percolator.Application/Apps/Chat`):
    * Create `ICertificateOrchestrator` with `Task RefreshLocalCertificateAsync(CancellationToken ct);`.
    * **Implementation:** 1. **Resolve Relay Destination:** Inject `IRelayTopology` and `IPeerRoutingProfileRepository`. Call the topology layer to resolve the designated relay peer, use the repository to extract its active endpoint network profile, and pull the target host string and port integer.
        2. **Generate Signature Challenge:** Generate the current UTC timestamp. Extract the cryptographic public key hash fingerprint token using `selfIdentity.GetActiveKey(timestamp).Fingerprint` (Rule 6 compliance). Convert to base64 string and combine with timestamp into a uniform byte buffer challenge payload. Inject `ILocalIdentitySigner` and invoke `await _localIdentitySigner.SignWithLocalIdentityKeyAsync(combinedPayload, ct);` to securely sign the payload.
        3. **Execute Transport Call:** Inject `IRelayTransportClient`. Call `await _relayTransportClient.FetchCertificateAsync(host, port, localPkh, timestamp, signature, ct);`.
        4. **Store Result:** The transport client returns a rich `DeliveryCertificate` domain record. Save it to `IDeliveryCertificateStore`.
* **The Gateway Implementation (`Percolator.Infrastructure/Network/Grpc`):**
    * Implement `RelayTransportClient` inheriting from `IRelayTransportClient`.
    * Inject `IPeerGrpcChannelFactory` into the constructor.
    * **Call Execution:** Follow the codebase connection pool pattern. Instantiate `var channel = _channelFactory.CreateChannel(new DnsEndPoint(targetHost, targetPort));` and wrap it inside a temporary `var client = new TransportService.TransportServiceClient(channel);`.
    * **Metadata Header Mapping:** Construct a new gRPC `Metadata` block. Append the challenge parameters as strings/binary data frames matching the contract specs. Dispatch the request using a constructed `CallOptions` block containing the metadata and the cancellation token to call `await client.GetDeliveryCertificateAsync(new GetDeliveryCertificateRequest(), callOptions);`.
    * **Response Parsing:** Parse the protobuf response directly into rich domain types using `DeliveryCertificatePayloadBytes.FromBytes()` and `Signature.FromBytes()`. Read expiration invariants directly from the domain concept wrapper using `payload.Span.Slice(16, 8)`. Return the constructed `DeliveryCertificate` domain record.
* The Background Worker (`Percolator.Infrastructure/Chat`):
    * Implement `DeliveryCertificateRefreshWorker : IHostedService`.
    * **Logic:** Hook cleanly into `IHostApplicationLifetime.ApplicationStarted`. Run an asynchronous processing loop bounded by the cancellation token. Inside the loop, create an `AsyncServiceScope`, resolve `ICertificateOrchestrator`, and invoke `RefreshLocalCertificateAsync()`. Await `Task.Delay(TimeSpan.FromHours(20), ct);` to trigger updates reliably before the 24-hour expiration window closes.

**Testing Requirements (Chunk 3):**
- `PeerAuthenticationService_AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenTimestampIsExpired` - Test that AuthenticateDeliveryCertificateRequestAsync returns false when the request timestamp is older than 60 seconds.
- `PeerAuthenticationService_AuthenticateDeliveryCertificateRequest_ReturnsFalse_WhenPeerNotFound` - Test that AuthenticateDeliveryCertificateRequestAsync returns false when the peer PKH lookup returns null.
- `AuthenticateDeliveryCertificateRequest_DoesNotThrow_WhenAllInputsAreValid` - Test that the authentication flow doesn't throw exceptions when provided with valid inputs (PKH, timestamp, signature). Note: This tests the flow, not cryptographic correctness (which belongs in integration tests).

---

## Chunk 3.1
### Feature Implementation Request: Signal Protocol Chunk 3.1 (Micro-PKI Primitives & Refresh Worker)
You are to implement Chunk 3.1 of our Signal Protocol integration for Percolator, a C# .NET 9 application built on a strict, "Shared Nothing" Modular Monolith architecture.

The Goal: Complete the Micro-PKI infrastructure that was de-scoped from Chunk 3. This includes implementing the Ed25519 native interop wrappers, saving the Relay Root Key into the persistence layer, and building the background worker that proactively refreshes the local delivery certificate.

Architectural Constraints (CRITICAL):
* **Strict DDD Context Mapping (No Project Coupling):** `Percolator.Identity` and `Percolator.Cryptography` have zero project dependencies on each other. Do NOT add references between them. We will duplicate the `RelayRootKeyBytes` primitive in both domains. The Application layer (`Percolator.Application`) will act as the Anti-Corruption Layer, translating between `Percolator.Identity.RelayRootKeyBytes` and `Percolator.Cryptography.RelayRootKeyBytes` using zero-allocation `.Span` and `.FromSpan()` methods.
* **Dependency Inversion Principle:** `Signal.Interop` is strictly an Infrastructure concern and is only referenced by `Percolator.Infrastructure`. `Percolator.Cryptography` cannot take a dependency on it. The `IEd25519CryptographyService` interface and `RelayRootKeyBytes` primitive MUST live in `Percolator.Cryptography` (100% free of `Signal.Interop` references). The implementation MUST live in `Percolator.Infrastructure` (e.g., `NativeEd25519CryptographyService`) and will be registered in the DI container to satisfy the interface.
* **Native FFI Memory Lifecycle:** The `Signal.Interop.SignalCrypto` Ed25519 methods are allocation-free. The C# caller must allocate the buffers. The infrastructure implementation should allocate `byte[]` buffers (or use `stackalloc` where appropriate), pass them as `Span<byte>` to the interop layer, and instantly wrap the results in our DDD primitives via `.FromBytesOwned()`.
* **Graceful Worker Shutdown:** The worker must handle `OperationCanceledException` explicitly to prevent hanging shutdown. Catch it *before* the general exception catch, break the loop cleanly, and do not log as an error.
* **No Raw Byte Arrays in Service Contracts:** The Application layer must use fully-qualified domain primitive types (e.g., `RelayRootKeyBytes`, `Ed25519SignatureBytes`).

Implementation Requirements

1. The Cryptography Domain (`Percolator.Cryptography`)
* Note that `Ed25519PublicKeyBytes` and `Ed25519SignatureBytes` already exist.
* The current `IEd25519CryptographyService` and `Ed25519CryptographyService` incorrectly use `ECDiffieHellman` and `ECDsa.Create()` to try and simulate Ed25519. This is wrong.
* **DELETE** the existing `Ed25519CryptographyService.cs` implementation from `Percolator.Cryptography` - it will be replaced by an infrastructure implementation.
* Add `RelayRootKeyBytes` as a new local primitive in `Percolator.Cryptography`: `[ByteArray(length: 32)] public sealed partial record RelayRootKeyBytes;`. Note that this name implies the private key in this context.
* Update `IEd25519CryptographyService` to the following contract (this interface stays in `Percolator.Cryptography`):
  ```csharp
  public interface IEd25519CryptographyService
  {
      void GenerateKeyPair(out RelayRootKeyBytes privateKey, out Ed25519PublicKeyBytes publicKey);
      Ed25519SignatureBytes Sign(ReadOnlySpan<byte> message, RelayRootKeyBytes privateKey);
      bool Verify(Ed25519PublicKeyBytes publicKey, ReadOnlySpan<byte> message, Ed25519SignatureBytes signature);
  }
  ```
* **DO NOT** implement this interface in `Percolator.Cryptography` - the implementation must live in `Percolator.Infrastructure` to avoid coupling to `Signal.Interop`.

2. Infrastructure Implementation (`Percolator.Infrastructure/Cryptography`)
* Create a new folder `Percolator.Infrastructure/Cryptography/` if it doesn't exist.
* Create `NativeEd25519CryptographyService.cs` in this folder.
* This class must implement `Percolator.Cryptography.IEd25519CryptographyService`.
* Implement the methods using the static methods on `Signal.Interop.SignalCrypto` (`GenerateEd25519KeyPair`, `Ed25519Sign`, `Ed25519Verify`).
  - For `GenerateKeyPair`: Allocate two 32-byte `byte[]` arrays, pass them as `Span<byte>` to `Signal.Interop.SignalCrypto.GenerateEd25519KeyPair`, then wrap the results using `RelayRootKeyBytes.FromBytesOwned()` and `Ed25519PublicKeyBytes.FromBytesOwned()`.
  - For `Sign`: Allocate a 64-byte `byte[]` for the signature, pass `privateKey.Span`, `message`, and the signature span to `Signal.Interop.SignalCrypto.Ed25519Sign`, then wrap using `Ed25519SignatureBytes.FromBytesOwned()`.
  - For `Verify`: Pass `publicKey.Span`, `message`, and `signature.Span` directly to `Signal.Interop.SignalCrypto.Ed25519Verify` and return the boolean result.
  - All native interop calls may throw `CryptographicException` on failure. The service must not catch these; let them propagate to the caller for proper error handling.

3. Dependency Injection Registration (`Percolator.Infrastructure`)
* Locate the DI registration extension for infrastructure services (e.g., `InfrastructureServiceCollectionExtensions.cs` or similar).
* Add a registration mapping `IEd25519CryptographyService` to `NativeEd25519CryptographyService` as a singleton or scoped service (based on the service's statelessness - singleton is appropriate since it has no state).
* Example: `services.AddSingleton<IEd25519CryptographyService, NativeEd25519CryptographyService>();`

4. Identity Domain & Persistence (`Percolator.Identity` & `Percolator.Infrastructure`)
* The `RelayRootKeyBytes` primitive already exists in `Percolator.Identity`.
* The `SelfIdentity` aggregate already has `RelayDeliveryRootKey` and `EnableRelayMode`.
* The `SelfIdentityDbo` already has `RelayDeliveryRootKey`.
* **Fix the bug in `SqliteSelfIdentityDomainRepository.cs`**: Inside the `SaveAsync` method, the `RelayDeliveryRootKey` is not currently being synchronized from the aggregate `self` to the tracked entity `current`. You must map `current.RelayDeliveryRootKey = self.RelayDeliveryRootKey?.ToArray();`.
* **Fix the bug in `SqliteSelfIdentityDomainRepository.cs`**: Inside the `Map` method, the `RelayDeliveryRootKey` is not currently being rehydrated from the database onto the aggregate. If `dbo.RelayDeliveryRootKey` is not null, call `aggregate.EnableRelayMode(Percolator.Identity.RelayRootKeyBytes.FromSpan(dbo.RelayDeliveryRootKey));` after `GetKeys` is populated.

5. Application Layer Anti-Corruption (`Percolator.Application/Chat`)
* The `LocalIdentitySigner` class currently uses `Signal.Interop.SignalCrypto.Ed25519Sign` directly. This is acceptable for now, but we must ensure that when it needs to use the new `IEd25519CryptographyService`, it performs zero-allocation translation between the two `RelayRootKeyBytes` primitives.
* Translation helper (if needed): To convert from `Percolator.Identity.RelayRootKeyBytes` to `Percolator.Cryptography.RelayRootKeyBytes`, use `Percolator.Cryptography.RelayRootKeyBytes.FromSpan(identityKey.Span)`. This is zero-allocation.
* No new Application layer code is required for Chunk 3.1 unless `LocalIdentitySigner` needs to be refactored to use `IEd25519CryptographyService`. The current direct interop usage is acceptable.

6. Background Certificate Refresh Worker (`Percolator.Infrastructure/Chat`)
* The `DeliveryCertificateRefreshWorker` file exists but it does not execute `RefreshLocalCertificateAsync` immediately on application startup, it waits 20 hours.
* We need the worker to attempt to fetch a certificate immediately when it starts, so that a fresh boot of the application will authenticate with the relay.
* Modify `RunRefreshLoopAsync` to:
  1. Attempt to refresh the certificate using the `ICertificateOrchestrator` inside a `try/catch`.
  2. **CRITICAL:** Catch `OperationCanceledException` *before* the general exception catch. When caught, log a debug/info message and break the loop cleanly (do not retry).
  3. **CRITICAL:** Catch `CryptographicException` *before* the general exception catch. This indicates a permanent configuration error (e.g., corrupted key material). Log as a critical error and break the loop - do not retry.
  4. If successful, delay for 20 hours (`Task.Delay(TimeSpan.FromHours(20), ct)`).
  5. If any other exception is thrown, log it as an error and delay for 5 minutes (`Task.Delay(TimeSpan.FromMinutes(5), ct)`).
  6. Ensure the loop continues running for non-cancellation, non-crypto exceptions.

**Testing Requirements (Chunk 3.1):**
- `Ed25519CryptographyService_GenerateKeyPair_CreatesValidKeys` - Test that `GenerateKeyPair` returns non-null, correctly sized primitive byte arrays that are not all zeros.
- `Ed25519CryptographyService_SignAndVerify_RoundTripsSuccessfully` - Test that signing a byte array with a generated private key and verifying it with the corresponding public key returns true.
- `Ed25519CryptographyService_Verify_ReturnsFalse_ForInvalidSignature` - Test that verification returns false when the signature is tampered with.
- `Ed25519CryptographyService_GenerateKeyPair_ThrowsOnInvalidBufferSizes` - Test that passing incorrectly sized spans throws `ArgumentException`.
- `SelfIdentityDomainRepository_RelayMode_PersistsAndRehydrates` - Add a test (similar to `Save_and_GetById_round_trips_ActiveIdentityKeySpki`) in `SelfIdentityDomainRepositoryTests.cs` to ensure that calling `EnableRelayMode` correctly saves the `RelayRootKeyBytes` to the SQLite database and rehydrates it upon load.
- `DeliveryCertificateRefreshWorker_Shutdown_CancelsGracefully` - Test that when `OperationCanceledException` is thrown during `Task.Delay`, the worker exits immediately without logging an error or triggering a backoff delay.

---
# IMPLEMENTATION PROMPT: CHUNK 4 - Group V2 Relay Ledger & Fan-Out

**Context:**
You are implementing the Signal Protocol Group V2 Relay Ledger for Percolator. The domain is fully isolated.

**CRITICAL ARCHITECTURAL RULES:**
1. **Concurrency (Manual Tracking):** DO NOT use `[ConcurrencyCheck]` or `.IsConcurrencyToken()`. You must perform manual version checking in memory.
2. **Locking:** Use a `private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();` to lock operations *per ConversationId*. Do NOT attempt to bound or dispose the semaphores (this is a desktop app; the memory footprint is negligible and disposal causes race conditions).
3. **Transaction Encapsulation:** The Application layer MUST NOT reference `DbContext` or manage transactions. We will use an `IRelayMessagePublisher` implemented in the Infrastructure layer to ensure the Ledger Save and Message Queue inserts are atomically bound.
4. **Interop Style:** Mirror the `ZkgroupCryptographyService.cs` pattern: instantiate a new `SafeHandle` per native call and immediately wrap it in a `using` block.
5. **Time Determinism:** Cryptographic validation paths require a timestamp. You MUST inject the `.NET 8 TimeProvider` and use `(ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds()`. Do NOT use `DateTime.UtcNow`.

---

### Task 1: Exceptions, Cryptography & Identity Foundations

**1. Domain Exceptions (`Percolator.Chat`):**
* Create `DomainException.cs` in the ROOT of `Percolator.Chat`:
    ```csharp
    namespace Percolator.Chat;
    public abstract class DomainException : Exception
    {
        protected DomainException(string message) : base(message) { }
        protected DomainException(string message, Exception innerException) : base(message, innerException) { }
    }
    ```
* Create `EpochConflictDomainException.cs` and `UnauthorizedDomainException.cs` inside `Percolator.Chat/GroupLedger/`. They must inherit from `DomainException`.

**2. Primitives (`Percolator.Cryptography`):**
* Create the following files in the ROOT of `Percolator.Cryptography` (do NOT put them in the `Primitives` folder):
    ```csharp
    using Percolator.SourceGenerators;
    namespace Percolator.Cryptography;

    [ByteArray(minLength: 1, maxLength: 5000)] // Let generator handle bounds if exact length is unknown
    public sealed partial record ZkPresentationBytes;

    [ByteArray(length: 32)] 
    public sealed partial record ZkServerSecretParamsSeedBytes;

    [ByteArray(minLength: 1, maxLength: 500)]
    public sealed partial record ZkGroupPublicParamsBytes;
    ```

**3. Identity Expansion (`Percolator.Infrastructure` & `Percolator.Application`):**
* **DBO Update:** Open `SelfIdentityDbo.cs` and add: `public byte[]? ZkServerSecretParamsSeed { get; set; }`. (Note: Do not write EF migrations; the DB will be recreated manually).
* **Query Interface:** Open `ISelfIdentityQueries.cs` in `Percolator.Application` and add: `Task<ZkServerSecretParamsSeedBytes?> GetZkServerSecretParamsSeedAsync(CancellationToken ct);`
* **Query Implementation:** Open `SelfIdentityQueries.cs` in `Percolator.Infrastructure` and implement the interface using `.AsNoTracking().Select(x => x.ZkServerSecretParamsSeed)`. Convert the returned byte array using `ZkServerSecretParamsSeedBytes.FromBytesOwned(...)`.

**4. Cryptography Service (`ZkgroupCryptographyService.cs`):**
* If `Signal.Interop.SignalCrypto.DeserializeGroupPublicParams` or `VerifyAuthCredentialWithPniPresentation` are missing from the C# wrapper, map them via `[DllImport]`.
* Implement `bool VerifyGroupPresentation(ZkPresentationBytes presentation, ZkServerSecretParamsSeedBytes serverSecretSeed, ZkGroupPublicParamsBytes groupPublic, ulong redemptionTimeEpochSeconds)`.
* **CRITICAL INTEROP PATTERN:** You must deserialize the byte arrays into `SafeHandle`s before calling the verify method. Mirror this exact pattern:
    ```csharp
    // 1. Deserialize the handles using Signal.Interop methods
    using var presentationHandle = Signal.Interop.SignalCrypto.DeserializeAuthCredentialWithPniPresentation(presentation.Span);
    using var serverSecretParamsHandle = Signal.Interop.SignalCrypto.DeserializeServerSecretParams(serverSecretSeed.Span); // (or derive them if a derive method exists)
    using var groupPublicParamsHandle = Signal.Interop.SignalCrypto.DeserializeGroupPublicParams(groupPublic.Span);
    
    // 2. Call the verify method
    try 
    {
        Signal.Interop.SignalCrypto.VerifyAuthCredentialWithPniPresentation(
            presentationHandle, 
            serverSecretParamsHandle, 
            groupPublicParamsHandle, 
            redemptionTimeEpochSeconds
        );
        return true;
    } 
    catch (Exception) // Catch the interop/verification exception 
    {
        return false;
    }
    ```
* Ensure `.NET 8 TimeProvider` is injected into the service that calls this, and pass `(ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds()` to the `redemptionTimeEpochSeconds` parameter.
### Task 2: Ledger Domain & CQRS Query Interfaces

**1. Local Domain Primitives (`Percolator.Chat/GroupLedger/`):**
* To maintain the "Shared Nothing" boundary, `Percolator.Chat` CANNOT reference `Percolator.Cryptography`.
* Define a local `[ByteArray]` primitive to hold the public params within the Chat domain. Create `RelayGroupPublicParamsBytes.cs`:
    ```csharp
    using Percolator.SourceGenerators;
    namespace Percolator.Chat.GroupLedger;

    [ByteArray(minLength: 1, maxLength: 500)]
    public sealed partial record RelayGroupPublicParamsBytes;
    ```

**2. Aggregate Root (`Percolator.Chat/GroupLedger/RelayGroupLedger.cs`):**
* Create this as a `public sealed class` with no base class or marker interfaces.
* **Imports:** Ensure you include `using Percolator.Chat.Messaging.ValueObjects;` and `using Percolator.Chat.GroupMembership;`.
* **Properties (Private Setters):**
    * `ConversationId ConversationId { get; private set; }`
    * `uint CurrentEpoch { get; private set; }`
    * `RelayGroupPublicParamsBytes GroupPublicParams { get; private set; }`
    * `int Version { get; private set; }`
* **Constructor:** Create a single `public` constructor that accepts all four properties and assigns them. **Do NOT create a parameterless constructor for EF Core.**
* **Behavior:** ```csharp
  public void AdvanceEpoch(uint requestedEpoch)
  {
  if (requestedEpoch <= CurrentEpoch)
  throw new EpochConflictDomainException($"Requested epoch {requestedEpoch} is stale.");
  CurrentEpoch = requestedEpoch;
  }
    ```

**3. Domain Repository Interfaces (`Percolator.Chat/GroupLedger/`):**
* Create `IRelayGroupLedgerRepository.cs`:
    ```csharp
    public interface IRelayGroupLedgerRepository
    {
        Task<RelayGroupLedger?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken);
    }
    ```
* Create `IRelayMessagePublisher.cs` (Abstracts the atomic transaction):
    ```csharp
    public interface IRelayMessagePublisher
    {
        Task PublishAtomicAsync(RelayGroupLedger ledger, IReadOnlyList<ChatPeerId> recipients, QueuedPayloadBytes payload, CancellationToken cancellationToken);
    }
    ```

**4. CQRS Query Interfaces (`Percolator.Application/Chat/`):**
* *Rule: These interfaces belong in the Application layer. Do NOT wrap these simple reads in MediatR queries; they will be injected directly into the gRPC services.*
* Create `IRelayRosterQueries.cs` (For fast fan-out routing):
    ```csharp
    using Percolator.Chat.GroupMembership; 
    namespace Percolator.Application.Chat;

    public interface IRelayRosterQueries
    {
        Task<IReadOnlyList<ChatPeerId>> GetMemberPeerIdsAsync(Guid conversationId, CancellationToken cancellationToken);
    }
    ```
* Create `IRelayGroupQueries.cs` (For clients resolving epoch conflicts):
    ```csharp
    using Percolator.Chat.GroupLedger;
    namespace Percolator.Application.Chat;

    public sealed record RelayGroupStateDto(uint Epoch, RelayGroupPublicParamsBytes GroupPublicParams);

    public interface IRelayGroupQueries
    {
        Task<RelayGroupStateDto?> GetGroupStateAsync(Guid conversationId, CancellationToken cancellationToken);
    }
    ```
### Task 3: Infrastructure & Atomicity

**1. Persistence Models & Configuration (`Percolator.Infrastructure`):**
* **DBO Definitions:**
    ```csharp
    public sealed class RelayGroupStateDbo
    {
        public Guid ConversationId { get; set; }
        public uint Epoch { get; set; }
        public byte[] GroupPublicParams { get; set; } = Array.Empty<byte>();
        public int Version { get; set; }
    }

    public sealed class RelayBlindedRosterDbo
    {
        public Guid ConversationId { get; set; }
        public Guid BlindedChatPeerId { get; set; }
        public DateTimeOffset AddedAtUtc { get; set; }
    }
    ```
* **DbContext Configuration:**
  Add to `PercolatorDbContext.cs`:
    ```csharp
    public DbSet<RelayGroupStateDbo> RelayGroupStates { get; set; } = null!;
    public DbSet<RelayBlindedRosterDbo> RelayBlindedRosters { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Existing mappings...

        modelBuilder.Entity<RelayGroupStateDbo>(entity => {
            entity.ToTable("RelayGroupStates");
            entity.HasKey(e => e.ConversationId);
            entity.Property(e => e.ConversationId).ValueGeneratedNever();
            entity.Property(e => e.GroupPublicParams).HasConversion(
                v => v, // RelayGroupPublicParamsBytes is handled via custom logic or simple array
                v => v);
        });

        modelBuilder.Entity<RelayBlindedRosterDbo>(entity => {
            entity.ToTable("RelayBlindedRosters");
            entity.HasKey(e => new { e.ConversationId, e.BlindedChatPeerId });
        });
    }
    ```

**2. Atomic Publisher (`SqliteRelayMessagePublisher.cs`):**
* **Implementation:**
    ```csharp
    public sealed class SqliteRelayMessagePublisher : IRelayMessagePublisher
    {
        private readonly PercolatorDbContext _db;
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

        public SqliteRelayMessagePublisher(PercolatorDbContext db) => _db = db;

        public async Task PublishAtomicAsync(
            RelayGroupLedger ledger, 
            IReadOnlyList<ChatPeerId> recipients, 
            QueuedPayloadBytes payload, 
            CancellationToken ct)
        {
            var gate = _locks.GetOrAdd(ledger.ConversationId.Value, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                // 1. Manual Version Check
                var dbo = await _db.RelayGroupStates.FindAsync(new object[] { ledger.ConversationId.Value }, ct);
                if (dbo == null || ledger.Version != dbo.Version)
                    throw new EpochConflictDomainException("Ledger version mismatch.");

                // 2. Update Ledger DBO
                dbo.Epoch = ledger.CurrentEpoch;
                dbo.GroupPublicParams = ledger.GroupPublicParams.ToArray();
                dbo.Version++;

                // 3. Fan-out: Map ChatPeerId (Domain) to MessageQueueItemDbo (Infrastructure)
                // Note: RecipientPeerId is the routing ID. 
                var queueItems = recipients.Select(peerId => new MessageQueueItemDbo {
                    Id = Guid.NewGuid(),
                    AckId = Guid.NewGuid(), 
                    RecipientPeerId = new PeerId(peerId.Value), // Conversion: Domain to Infrastructure Identity type
                    Blob = payload.ToArray(),
                    EnqueuedAtUtc = DateTimeOffset.UtcNow
                }).ToList();

                _db.MessageQueueItems.AddRange(queueItems);
                
                // 4. Atomic Commit
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }
    ```
* **Constraint:** This ensures that the ledger increment and the fan-out queue inserts are treated as a single atomic unit within the SQLite file, preventing any partial state in the event of a crash.

### Task 4: Application Orchestration (`Percolator.Application/Chat/`)

**1. The Relay Group Orchestrator (`RelayGroupOrchestrator.cs`):**
* Create this service to encapsulate coordination logic. It accepts only Domain primitives.
* **Namespace:** `Percolator.Application.Chat`
* **Interface (`IRelayGroupOrchestrator`):**
    ```csharp
    public interface IRelayGroupOrchestrator
    {
        Task PublishGroupRelayMessageAsync(
            ConversationId conversationId,
            uint requestedEpoch,
            ZkPresentationBytes presentation,
            CiphertextBytes ciphertext, // Strongly-typed domain primitive
            CancellationToken ct);
    }
    ```
* **Implementation (`RelayGroupOrchestrator`):**
    * Injects: `ISelfIdentityQueries`, `IGroupCryptographyService`, `IRelayGroupLedgerRepository`, `IRelayRosterQueries`, `IRelayMessagePublisher`, `TimeProvider`.
    * **Workflow:**
        1. **Authorizer:** `var seed = await _identityQueries.GetZkServerSecretParamsSeedAsync(ct);`
        2. **Auth Proof:** Pass the domain-typed `CiphertextBytes` to `_cryptoService.VerifyGroupPresentation(...)` using the injected `TimeProvider` for the `redemptionTimeEpochSeconds`. Throw `UnauthorizedDomainException` if verification fails.
        3. **Consensus:** 
            * `var ledger = await _ledgerRepository.GetByIdAsync(conversationId, ct);`
            * `ledger.AdvanceEpoch(requestedEpoch);`
        4. **Fan-out:**
            * `var peerIds = await _rosterQueries.GetMemberPeerIdsAsync(conversationId.Value, ct);`
            * `var payload = QueuedPayloadBytes.FromSpan(ciphertext.Span);` // Zero-allocation boundary copy
            * `await _publisher.PublishAtomicAsync(ledger, peerIds, payload, ct);`

**2. External Entry Point (gRPC Service):**
* **Responsibility:** Act as the *sole* translation layer. This is where the gRPC `ByteString` becomes a Domain `[ByteArray]` primitive via `FromBytesOwned`.
* **Implementation Pattern:**
    ```csharp
    public sealed class RelayGroupService : Messaging.RelayGroupService.RelayGroupServiceBase
    {
        private readonly IRelayGroupOrchestrator _orchestrator;

        public RelayGroupService(IRelayGroupOrchestrator orchestrator) => _orchestrator = orchestrator;

        public override async Task<ProvisionRelayGroupResponse> Publish(
            ProvisionRelayGroupRequest request, 
            ServerCallContext context)
        {
            // Boundary Conversion: Protobuf -> Domain Primitive (Defensive Copy)
            // Note: FromBytesOwned performs the defensive copy of the ToByteArray() result
            var ciphertext = CiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());
            
            await _orchestrator.PublishGroupRelayMessageAsync(
                new ConversationId(request.ConversationId.ToGuid()),
                presentation,
                ciphertext,
                context.CancellationToken);

            return new ProvisionRelayGroupResponse();
        }
    }
    ```

**3. Privacy & Safety Directives for Implementation:**
* **Boundary Enforcement:** The Orchestrator method `PublishGroupRelayMessageAsync` MUST NOT accept `byte[]`. It accepts `CiphertextBytes`.
* **Time Determinism:** The Orchestrator MUST pass `(ulong)_timeProvider.GetUtcNow().ToUnixTimeSeconds()` to the `VerifyGroupPresentation` method.
* **Shared Nothing:** The Orchestrator knows nothing of EF Core, SQL, or DBOs. It operates solely on `IRelayGroupLedgerRepository` and `IRelayMessagePublisher`.
* **Error Handling:** If `PublishAtomicAsync` or `VerifyGroupPresentation` throws a domain exception, it is allowed to bubble out of the orchestrator, where the gRPC service can map it to a specific gRPC error status (e.g., `Status(StatusCode.Unauthenticated, ...)`).
### Task 5: External Contracts & gRPC (`Percolator.Infrastructure`)

**1. Protobuf Definitions (`Percolator.Contracts/Protos/messaging.proto`):**
* The contract must exist in the `percolator.contracts` package.
* **Semantic Note:** The operation is named `Submit` to accurately reflect the intent of relaying a message, distinct from device-provisioning flows.

```protobuf
syntax = "proto3";

package percolator.contracts;
option csharp_namespace = "Percolator.Contracts";

service RelayGroupService {
    rpc Publish(SubmitGroupMessageRequest) returns (SubmitGroupMessageResponse);
}

message SubmitGroupMessageRequest {
    optional bytes conversation_id = 1;
    optional bytes presentation = 2;    // ZK Proof
    optional bytes ciphertext = 3;      // Encrypted payload
    optional uint32 epoch = 4;          // Target epoch for concurrency control
}

message SubmitGroupMessageResponse {
    optional bool success = 1;
}
```

**2. gRPC Infrastructure Registration (`Percolator.Infrastructure/Network/GrpcServerManager.cs`):**
* Use the established `PrimaryContainerServiceActivator` pattern to bridge gRPC services to the primary DI container.
* **Implementation Logic:**
```csharp
// Inside GrpcServerManager.cs, within the registration block:

// 1. Register service activator that bridges to the primary DI container
builder.Services.AddSingleton<IGrpcServiceActivator<RelayGroupService>>(
    new PrimaryContainerServiceActivator<RelayGroupService>(_primaryProvider));

// 2. Register with existing interceptor for identity readiness
builder.Services.AddGrpc().AddServiceOptions<RelayGroupService>(options =>
{
    options.Interceptors.Add<IdentityReadinessInterceptor>();
});

// 3. Map the service
app.MapGrpcService<RelayGroupService>();
```

**3. gRPC Service Implementation (`Percolator.Infrastructure/Services/RelayGroupService.cs`):**
* **Responsibility:** Act as the *sole* translation layer where Protobuf `ByteString` meets the Domain. Perform defensive copies immediately to ensure memory safety and fulfill the `[ByteArray]` pattern.
* **Implementation:**
```csharp
public sealed class RelayGroupService : Percolator.Contracts.RelayGroupService.RelayGroupServiceBase
{
    private readonly IRelayGroupOrchestrator _orchestrator;

    public RelayGroupService(IRelayGroupOrchestrator orchestrator) 
        => _orchestrator = orchestrator;

    public override async Task<SubmitGroupMessageResponse> Publish(
        SubmitGroupMessageRequest request, 
        ServerCallContext context)
    {
        try 
        {
            // Boundary Defensive Copy (Protobuf ByteString -> Domain Primitive)
            // Rule: Use DomainType.FromBytesOwned(byteString.ToByteArray())
            // This prevents memory corruption by taking ownership of the defensive copy.
            var conversationId = new ConversationId(new Guid(request.ConversationId.ToByteArray()));
            var presentation = ZkPresentationBytes.FromBytesOwned(request.Presentation.ToByteArray());
            var ciphertext = CiphertextBytes.FromBytesOwned(request.Ciphertext.ToByteArray());

            await _orchestrator.PublishGroupRelayMessageAsync(
                conversationId,
                request.Epoch,
                presentation,
                ciphertext,
                context.CancellationToken);

            return new SubmitGroupMessageResponse { Success = true };
        }
        catch (UnauthorizedDomainException ex)
        {
            // Map auth failure to Unauthenticated
            throw new RpcException(new Status(StatusCode.Unauthenticated, ex.Message));
        }
        catch (EpochConflictDomainException ex)
        {
            // Map concurrency/epoch conflict to Aborted
            throw new RpcException(new Status(StatusCode.Aborted, ex.Message));
        }
        catch (ArgumentException ex)
        {
            // Map validation errors to InvalidArgument
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception)
        {
            // Generic Internal Server Error to prevent leaking sensitive domain details
            throw new RpcException(new Status(StatusCode.Internal, "Internal relay error."));
        }
    }
}
```

**4. Privacy & Safety Directives:**
* **Isolation:** The gRPC service does not attempt to resolve the sender's identity. It strictly relies on the `ZkPresentationBytes`, which the `RelayGroupOrchestrator` validates via cryptographic proof.
* **Safety:** All Protobuf inputs are strictly converted to strongly-typed Domain Primitives at the entry point of the `Publish` method. No `byte[]` or `ByteString` should ever reach the Orchestrator or the Domain layers.
* **Determinism:** The `Orchestrator` handles all timing logic using the injected `TimeProvider`, ensuring gRPC dispatchers remain purely async and deterministic.
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