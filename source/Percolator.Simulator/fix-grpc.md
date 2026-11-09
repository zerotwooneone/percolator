# Percolator gRPC Hardening, Production Class Improvements & Simulator Crypto-Fidelity Plan (`fix-grpc.md`)

> [!IMPORTANT] Architectural Objective
> This plan remediates protocol, privacy, and wiring defects across the gRPC endpoints in `Percolator.Infrastructure`, enforces opt-in privacy controls in `Percolator.Apps.FileTransfer`, improves the real production infrastructure/domain classes (`TransportDispatcher`, `UnauthenticatedDeliveryEndpoint`, `SqliteRatchetSessionRepository`, `VaultStorageOptions`, `SqlCipherPragmaInterceptor`, `NetworkInterfaceEndpointDiscoveryPort`, `InMemoryRelayMailboxCapabilityStore`, `GroupCredentials`) so they support loopback/simulator and multi-session scenarios natively, and deletes redundant simulator workarounds (`SimulatorTransportDispatcher`, `SimulatorDirectIngressBridge`, `SimulatorRatchetSessionRepositoryDecorator`, `SimulatorSqlCipherPragmaInterceptor`, `RelayPreKeyAvailabilityAdapter`).
>
> **Anti-Shortcut Discipline (Zero Tolerance)**:
> In strict compliance with `.gemini/skills/anti-shortcut-discipline/SKILL.md`, this plan enforces:
> - **Rule 1 (No Nullable Required Parameters)**: All required dependencies in `TransportDispatcher` and other production classes must be non-nullable without default values (`= null`).
> - **Rule 2 (Observer Isolation - No RAM Peeking)**: Nodes never inspect or resolve remote hosts' DI containers (`r.Services.CreateScope()`); each node acts solely upon its own local state and in-band protocol arrivals.
> - **Rule 6 (Observable Failure Signaling)**: No silent failure swallows or false `Success = true` ACKs; all boundary errors return structured error codes and log to logger sinks.
> - **Rule 9 (Zero Service Locator)**: Explicit constructor injection for all services; zero `IServiceProvider` injection into application/domain logic.
> - **Rule 11 (Outbox Durability)**: All outbound messages stage as durable `OutboxJob` entities before transport dispatch.
> - **Rule 13 (Zero In-Process Transport Bypasses)**: All multi-node communication traverses real gRPC endpoints over loopback TLS.
> - **Rule 14 (No Coordinator Backdoors)**: Eliminate side-channel state dictionaries (e.g. `_groupRelays` in `GroupChatCoordinator`); state is stored in each persona's local repositories and resolved via protocol messages.
> - **Rule 15 (Deterministic Signal Awaiting)**: Synchronization occurs via reactive signals (`_stateBus.Signals`) and outbox flushes, never arbitrary `Task.Delay` sleeps.

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Refined)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Refined)
- [x] Lens 3: Threat Modeling & Security Posture (Status: Refined)
- [x] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Refined)
- [x] Lens 5: Cross-Layer Infrastructure Alignment (Status: Refined)
- [x] Implementation Gate: UNLOCKED

---

## 1. Scope & Feature Slice Framing (Lens 1)

### 1.1 Problem Statement: Identified gRPC Defects, Production Gaps & Simulator Shortcuts

#### A. Production gRPC & Protocol Defects (`Percolator.Infrastructure` & `Percolator.Apps.FileTransfer`)
1. **Unsealed Cleartext Metadata in `SealedWireFraming` (Signal Protocol Privacy Divergence)**:
   - `SealedWireFraming.Encode` in [`SealedEnvelopeUnwrapper.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs#L20-L78) currently writes `ChannelId` (bytes 1–16) and `SenderId` (bytes 17–32) in cleartext at the start of `EncryptedPackage`. Under TOFU TLS, any relay handling `EnqueueMailbox` can inspect bytes 1–32 to identify the exact sender and conversation channel of every message, defeating Sealed Sender privacy (`SYS-SEALED-SENDER`).
2. **Incomplete Production `TransportDispatcher` & `UnauthenticatedDeliveryEndpoint.DeliverDirect`**:
   - [`UnauthenticatedDeliveryEndpoint.DeliverDirect`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs#L43-L84) unwraps `SealedEnvelopeProto` and checks `IIngressFilterService.CheckIngressAllowedAsync`, but **never invokes `IInboundIngressPipeline.ProcessInboundAsync`** and silently returns `Success = true` when frame unwrapping fails.
   - Production [`TransportDispatcher`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/TransportDispatcher.cs#L55-L217) dispatches raw `job.Payload` without encoding the `SealedWireFraming` header, cannot resolve `DirectP2P`, `RelayedOneToOne`, or `RelayedGroup` endpoints when `DeliveryRoute` endpoints are null, and hardcodes `epoch = 0` on `RelayedGroup` ZK proof generation.
   - [`ChannelRelayResolver`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs#L217) is registered as `Scoped` instead of `Singleton`, losing its in-memory `ChannelId -> Uri` map across scopes.
3. **Production Infrastructure Gaps Forcing Simulator Workarounds**:
   - [`SqliteRatchetSessionRepository.SaveSessionAsync`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Persistence/Repositories/SqliteRatchetSessionRepository.cs#L84-L86) looks up sessions only by `SessionId`, throwing a SQLite `UNIQUE` constraint violation on `ix_vault_ratchet_route` `(OwnerIdentityId, OwnerDeviceId, RemotePeerId, RemoteDeviceId)` when a peer re-handshakes with a new `SessionId`.
   - [`VaultStorageOptions`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Vault/VaultStorageOptions.cs) and [`SqlCipherPragmaInterceptor`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Vault/SqlCipherPragmaInterceptor.cs) lack a `UseEncryption` flag to allow standard unencrypted WAL/Foreign-Key pragmas when running multi-node simulator SQLite databases without SQLCipher encryption.
   - [`NetworkInterfaceEndpointDiscoveryPort`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Plugins/FileTransfer/NetworkInterfaceEndpointDiscoveryPort.cs) hard-filters `IPAddress.IsLoopback(ip)` unless no LAN adapter is up, lacking an `allowLoopback` configuration flag for local/simulator P2P file transfers.
4. **Unauthenticated Blob Deletion & Unverified Content Hash (`UploadBlindBlob` / `DeleteBlindBlob`)**:
   - `DeleteBlindBlob` allows any unauthenticated caller with a 32-byte `BlobId` to delete blobs from the relay/peer store, violating Signal's immutable TTL attachment model.
   - `UploadBlindBlob` accepts a caller-asserted `BlobId` without verifying that `SHA256(stream) == BlobId`, enabling cache poisoning of content-addressed slots.
5. **Unauthenticated Mailbox Overwrite (`AuthenticatedRelayEndpoint.RegisterMailbox`) & Stream Closure**:
   - [`IRelayMailboxCapabilityStore.RegisterAsync`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/IRelayMailboxCapabilityStore.cs#L14-L20) unconditionally overwrites `DrainToken` if a `RoutingToken` already exists, allowing anyone who knows a peer's public `RoutingToken` and `DeliveryToken` to hijack their mailbox.
   - [`AuthenticatedRelayEndpoint.ConnectMailboxStream`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs#L76-L144) drains only the initial backlog and immediately exits instead of keeping a live push channel open and awaiting `MailboxAckProto` (`ack.QueueId`) before deleting queued envelopes.
6. **Missing ZK Proof Verification on Group Reads (`AnonymousGroupRelayEndpoint`)**:
   - `SubscribeGroupStream` and `FetchGroupState` accept `zk_presentation_proof` in their Protobuf requests but never call `RelayGroupLedger` to verify the proof, allowing anyone who learns a 16-byte `ConversationId` to harvest encrypted roster blobs and snoop group traffic metadata.
7. **Unauthenticated Directory Visibility Mutation (`NearbyDiscoveryEndpoint.RegisterVisibility`)**:
   - `RegisterVisibility` accepts a raw `peer_id` and `visibility` enum with no Ed25519 signature verification, allowing any caller to spoof a peer's directory visibility or force a discoverable user offline.
8. **File Transfer Opt-In Default Enforcement (`Percolator.Apps.FileTransfer`)**:
   - Because multi-stream P2P file transfer exposes peer IP addresses over direct TCP sockets and is less private than relayed chat, it must default to **disabled** at the application/domain boundary (`FileTransferIngressFilter.IsEnabled = false` and `SimulatedPersonaHost.AllowFileTransfers = false`), rejecting inbound `FileTransferOfferPayload` messages and outbound initiations unless explicitly enabled by the user.

#### B. Redundant Simulator Implementations & Shortcuts to Eliminate (`Percolator.Simulator`)
1. **Delete Redundant Simulator Infrastructure Classes**:
   - Delete [`SimulatorTransportDispatcher.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorTransportDispatcher.cs) and use production `TransportDispatcher` directly (with zero `_worldState` transport wrapper).
   - Delete [`SimulatorDirectIngressBridge.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorDirectIngressBridge.cs) and use production `IngressFilterService` and `UnauthenticatedDeliveryEndpoint`.
   - Delete `SimulatorRatchetSessionRepositoryDecorator` in [`SimulatorNotifyingHandshakeDecorator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorNotifyingHandshakeDecorator.cs#L24-L70) once `SqliteRatchetSessionRepository.SaveSessionAsync` is fixed.
   - Delete [`SimulatorSqlCipherPragmaInterceptor.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorSqlCipherPragmaInterceptor.cs) once `VaultStorageOptions.UseEncryption = false` is supported by production `SqlCipherPragmaInterceptor`.
   - Delete `RelayPreKeyAvailabilityAdapter` in [`SimulatedRelayHost.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs#L172) and use production `PreKeyAvailabilityAdapter`.
   - Refactor [`SimulatorNotifyingRelayCapabilityStoreDecorator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/SimulatorNotifyingRelayCapabilityStoreDecorator.cs) to wrap an inner `InMemoryRelayMailboxCapabilityStore` instead of duplicating its mailbox storage logic.
2. **Replace Out-of-Band PreKey, Group Provisioning & Swarm Shortcuts with Real gRPC/Ratchet Flows**:
   - [`DirectHandshakeOrchestrator`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs#L91) must fetch `PreKeyBundle` via `UnauthenticatedDeliveryServiceClient.FetchPreKeyBundleAsync` over loopback gRPC instead of calling `target.CreatePreKeyBundle()` in-memory.
   - [`GroupChatCoordinator`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/GroupMessaging/GroupChatCoordinator.cs#L133) must create group ledgers via `AnonymousGroupRelayServiceClient.CreateGroupAsync` over gRPC instead of calling `relay.ProvisionGroupLedgerAsync(...)` in-process, and eliminate coordinator state dictionary `_groupRelays`.
   - [`SwarmSimulationCoordinator`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/SwarmSimulation/SwarmSimulationCoordinator.cs) must enforce `FileTransferIngressFilter` locally on each persona, discover loopback candidates via `INetworkEndpointDiscoveryPort`, and invoke `IFileTransferActionHandler.InitiateTransferAsync` / `AcceptTransferAsync` in-band over the Double Ratchet channel (`AppId.FileTransferControl`) without cross-host RAM peeking.

### 1.2 Vertical Feature Slices & Architectural Organization

| Project | Feature Slice | Responsibilities in This Plan |
| :--- | :--- | :--- |
| **`Percolator.Domain`** | `Delivery/Hosting/` & `Security/Model/` | Add `RelayGroupLedger.VerifyReadPresentation` / `ComputeReadChallenge`, and add `CurrentEpoch` tracking to `GroupCredentials`. |
| **`Percolator.Infrastructure`** | `Transport/`, `Persistence/`, `Vault/`, `Plugins/` | AEAD-sealed `SealedWireFraming`, self-sufficient `TransportDispatcher` (resolving routes via non-null `IStreamRegistryManagement`, `IPeerMailboxRoutingRepository`, `IChannelRelayResolver`, and `IGroupCredentialsRepository`), `DeliverDirect` ingress pipeline execution, `SqliteRatchetSessionRepository` route upsert fix, `VaultStorageOptions.UseEncryption` flag, `NetworkInterfaceEndpointDiscoveryPort` `allowLoopback` flag, SHA-256 verified `UploadBlindBlob` (and removal of `DeleteBlindBlob` from endpoint & `RelayBlobClient`), authenticated `RegisterMailbox` ownership check, live `ConnectMailboxStream` push + ACK (`queue_id`), ZK-verified `FetchGroupState` / `SubscribeGroupStream` + `CreateGroup`, and Ed25519-signed `RegisterVisibility`. |
| **`Percolator.Apps.FileTransfer`** | `Features/IngressControl/` & `Features/TransferNegotiation/` | Enforce opt-in default (`IsEnabled = false`) in `FileTransferIngressFilter` and `FileTransferActionHandler` with explicit `FILE_TRANSFER_DISABLED_BY_PRIVACY_POLICY` error reporting, and register concrete `FileTransferIngressFilter` in DI. |
| **`Percolator.Simulator`** | `Features/DirectChat/`, `Features/GroupMessaging/`, `Features/SwarmSimulation/`, `Features/Topology/`, `Scenarios/` | Delete 5 redundant simulator classes/decorators, wrap `InMemoryRelayMailboxCapabilityStore`, delete `_groupRelays` backdoor dictionary, fetch prekey bundles and create group ledgers via loopback gRPC, negotiate file transfers in-band over Double Ratchet without RAM peeking, and pass all 8 headless replay scenarios (`Scenarios/01-*.json` through `08-*.json`). |

### 1.3 Explicit Non-Goals
- **No External CA or Public PKI**: Transport remains TOFU TLS 1.3 with ephemeral P-256 certificates; all authentication and sealing are enforced cryptographically at the application/framing layer.
- **No New Cryptographic Libraries**: All sealing, signing, and hashing use existing `ICryptoEngine`, `IZkProofEngine`, and `System.Security.Cryptography` (`AesGcm`, `HKDF`, `SHA256`, `IncrementalHash`) primitives.

---

## 2. Onion Architecture & Technical Specifications (Lens 2)

### 2.1 Encrypted Sealed Wire Framing (`SealedWireFraming` in `SealedEnvelopeUnwrapper.cs`)

#### Exact Wire Layout (`SealedWireFraming`)
- **Enum `WireFrameKind`** (unchanged values in [`SealedEnvelopeUnwrapper.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs#L13-L18)):
  - `DirectRatchet = 0x01`
  - `GroupBroadcast = 0x02`
  - `X3dhHandshake = 0x03`
- **Outer Frame Header (29 bytes) + Encrypted Inner Frame (`53 + N` bytes = `82 + N` total bytes)**:
  - `Byte 0` (1B): `SealedMagicByte = 0x53` (`'S'`) indicating an AEAD-sealed frame (note: in `SealedEnvelopeUnwrapper.Unwrap`, check `if (envelope.Ciphertext.Length > 0 && envelope.Ciphertext.Span[0] != SealedWireFraming.SealedMagicByte)` before attempting `SealedEnvelopeProto.Parser.ParseFrom`, because `0x53 > 0x03`!).
  - `Bytes 1..12` (12B): Random 96-bit AES-GCM Nonce (`RandomNumberGenerator.Fill`).
  - `Bytes 13..28` (16B): 128-bit AES-GCM Authentication Tag.
  - `Bytes 29..81` (53B): Encrypted Inner Metadata Header:
    - `Inner[0]` (1B): `WireFrameKind` (`0x01 = DirectRatchet`, `0x02 = GroupBroadcast`, `0x03 = X3dhHandshake`).
    - `Inner[1..16]` (16B): `ChannelId` (`channelId.TryWriteBytes`).
    - `Inner[17..32]` (16B): `SenderId` (`senderId.TryWriteBytes`).
    - `Inner[33..36]` (4B): `SenderDeviceId` (`UInt32` Little-Endian).
    - `Inner[37..48]` (12B): Reserved (`0x00` bytes).
    - `Inner[49..52]` (4B): `PayloadLength` (`UInt32` Little-Endian = `(uint)packedWireBytes.Length`).
  - `Bytes 82..(82 + N)` (`N` bytes): Encrypted Inner Payload (`packedWireBytes`).
- **Minimum Valid Frame Size**: `HeaderSize = 82` bytes (`29` outer AEAD header + `53` encrypted inner metadata header).

#### Exact Key Derivation & Method Signatures
- Keep the exact existing public method signatures on `SealedWireFraming` so all existing callers compile without changes:
  ```csharp
  public static class SealedWireFraming
  {
      public const byte SealedMagicByte = 0x53;
      public const int InnerHeaderSize = 53;
      public const int OuterAeadHeaderSize = 1 + 12 + 16; // 29 bytes
      public const int HeaderSize = OuterAeadHeaderSize + InnerHeaderSize; // 82 bytes

      private static readonly byte[] DefaultKeyIkm =
          SHA256.HashData("Percolator.SealedWireEnvelope.DefaultKey.v1"u8);

      public static byte[] Encode(
          WireFrameKind kind,
          ChannelId channelId,
          PublicIdentityId senderId,
          DeviceId senderDeviceId,
          ReadOnlyMemory<byte> packedWireBytes)
  ```
- **Key Derivation**:
  - Derive a 32-byte AES-256 key per frame via `HKDF.DeriveKey(HashAlgorithmName.SHA256, DefaultKeyIkm, outputLength: 32, salt: nonce12, info: "Percolator.SealedWireFraming.AEAD.v2"u8)`.
  - Encrypt `plaintext` (`53 + packedWireBytes.Length` bytes) using `using var aes = new AesGcm(derivedKey, tagSizeInBytes: 16);` with `nonce = output.AsSpan(1, 12)`, `tag = output.AsSpan(13, 16)`, and `associatedData = output.AsSpan(0, 1)`.
- **Decode Validation Rules (Anti-Shortcut Rule 6 - Explicit Failure Signaling)**:
  1. If `frameBytes.Length < HeaderSize` (`82`), return `DomainResult.Failure(new DomainError("FRAME_TOO_SHORT", ...))`.
  2. If `frameBytes.Span[0] != SealedMagicByte` (`0x53`), return `DomainResult.Failure(new DomainError("UNKNOWN_FRAME_KIND", ...))`.
  3. Derive the 32-byte key from `nonce = frameBytes.Span.Slice(1, 12)` and decrypt `frameBytes.Span.Slice(29)` with `tag = frameBytes.Span.Slice(13, 16)` and `associatedData = frameBytes.Span.Slice(0, 1)`. Catch `CryptographicException` and return `DomainResult.Failure(new DomainError("FRAME_TRUNCATED", "Sealed frame authentication failed or frame was truncated/corrupted."))`.
  4. Check `plaintext[0]` is `WireFrameKind.DirectRatchet`, `WireFrameKind.GroupBroadcast`, or `WireFrameKind.X3dhHandshake`; if not, return `DomainResult.Failure(new DomainError("UNKNOWN_FRAME_KIND", ...))`.
  5. Read `uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(plaintext.Slice(49, 4))`; if `InnerHeaderSize + payloadLength > plaintext.Length`, return `DomainResult.Failure(new DomainError("FRAME_TRUNCATED", ...))`.

### 2.2 Self-Sufficient Production Classes (`Percolator.Domain` & `Percolator.Infrastructure`)

1. **`StreamRegistry` (`IStreamRegistryManagement`) Direct Endpoint Resolution**:
   - Add `void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint);` and `Uri? GetDirectEndpoint(PublicIdentityId targetId);` to `IStreamRegistryManagement` and implement them in `StreamRegistry` using a `ConcurrentDictionary<PublicIdentityId, Uri> _directEndpoints = new();`.
2. **`GroupCredentials` Epoch Persistence (`Percolator.Domain` & `SqliteGroupCredentialsRepository`)**:
   - Add `public EpochNumber CurrentEpoch { get; private set; }` (default `EpochNumber.Genesis`) and `public void UpdateEpoch(EpochNumber newEpoch) => CurrentEpoch = newEpoch;` to `GroupCredentials` (with optional `EpochNumber currentEpoch = default` parameter on `GroupCredentials(...)` and `GroupCredentials.CreateGenesis(...)` where `default` normalizes to `currentEpoch.Value == 0 ? EpochNumber.Genesis : currentEpoch`).
   - In `SqliteGroupCredentialsRepository.SaveAsync`: when `credentials.CurrentEpoch.Value > 0`, prefix `entity.AuthCredentialMac` with `"EPCH"u8` (4 bytes) + `credentials.CurrentEpoch.Value` (4 bytes Little-Endian) followed by `credentials.AuthCredentialMac.Span`; in `GetAsync`, if `entity.AuthCredentialMac.Length > 8 && entity.AuthCredentialMac.AsSpan(0, 4).SequenceEqual("EPCH"u8)`, read `uint epoch = BinaryPrimitives.ReadUInt32LittleEndian(entity.AuthCredentialMac.AsSpan(4, 4))` and slice `entity.AuthCredentialMac.AsSpan(8)` so `CurrentEpoch` round-trips through SQLite with zero schema migration changes.
3. **`TransportDispatcher` Route & Epoch Resolution (Anti-Shortcut Rule 1 - Non-Nullable Dependencies)**:
   - Reject lazy `= null` parameters. Declare mandatory, non-null dependencies in `TransportDispatcher`:
     ```csharp
     public TransportDispatcher(
         ITransportClientInvoker invoker,
         IZkProofEngine zkProofEngine,
         IGroupCredentialsRepository groupCredentialsRepository,
         IPeerMailboxRoutingRepository mailboxRoutingRepository,
         IChannelRelayResolver channelRelayResolver,
         IStreamRegistryManagement streamRegistry)
     {
         _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
         _zkProofEngine = zkProofEngine ?? throw new ArgumentNullException(nameof(zkProofEngine));
         _groupCredentialsRepository = groupCredentialsRepository ?? throw new ArgumentNullException(nameof(groupCredentialsRepository));
         _mailboxRoutingRepository = mailboxRoutingRepository ?? throw new ArgumentNullException(nameof(mailboxRoutingRepository));
         _channelRelayResolver = channelRelayResolver ?? throw new ArgumentNullException(nameof(channelRelayResolver));
         _streamRegistry = streamRegistry ?? throw new ArgumentNullException(nameof(streamRegistry));
     }
     ```
   - For `DirectP2P`: if `route.DirectEndpoint == null && job.RecipientIdentityId.HasValue`, resolve `directEndpoint = _streamRegistry.GetDirectEndpoint(job.RecipientIdentityId.Value);`. If endpoint is still null, fail with `DomainError("DIRECT_ENDPOINT_NOT_RESOLVED", ...)`.
   - For `RelayedOneToOne`: if `route.RelayEndpoint == null || !route.TargetToken.HasValue || route.TargetDeliveryToken == null`, resolve from `await _mailboxRoutingRepository.GetByPeerIdAsync(job.RecipientIdentityId.Value, ct)`.
   - For `RelayedGroup`: if `route.RelayEndpoint == null`, resolve `relayUri = await _channelRelayResolver.ResolveRelayForChannelAsync(job.ChannelId, ct)`; read `epoch = creds.CurrentEpoch.Value` from `_groupCredentialsRepository.GetAsync(job.ChannelId, ct)`.
   - In `InfrastructureServiceCollectionExtensions.AddAuthenticatedInfrastructure`, change `services.AddScoped<IChannelRelayResolver, ChannelRelayResolver>()` to `services.AddSingleton<IChannelRelayResolver, ChannelRelayResolver>()`.
4. **`SqliteRatchetSessionRepository.SaveSessionAsync` Route Upsert Fix**:
   - In `SqliteRatchetSessionRepository.SaveSessionAsync`, query any existing rows in `_dbContext.VaultRatchetSessions` matching `(OwnerIdentityId == ownerGuid && OwnerDeviceId == ownerDevVal && RemotePeerId == remoteGuid && RemoteDeviceId == remoteDevVal && SessionId != sessionIdStr)` and remove them before inserting/updating `sessionIdStr`, preventing `UNIQUE constraint failed` on `ix_vault_ratchet_route` when a peer re-handshakes.
5. **`VaultStorageOptions.UseEncryption` & `NetworkInterfaceEndpointDiscoveryPort` `allowLoopback`**:
   - Add `public bool UseEncryption { get; set; } = true;` to `VaultStorageOptions`, and inject `VaultStorageOptions? options = null` into `SqlCipherPragmaInterceptor` so when `options is { UseEncryption: false }`, it skips `PRAGMA cipher_memory_security = ON;` and `_keyHandle.ApplyKeyToConnection(sqliteConn)` and applies only `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;`.
   - Update `NetworkInterfaceEndpointDiscoveryPort(int listenPort = 50001, string relayBaseUri = "https://relay.percolator.net/pipes", bool allowLoopback = false)` so when `_allowLoopback` is true, `DiscoverLocalCandidatesAsync` always appends `new DirectEndpointCandidate(EndpointHost.Parse("127.0.0.1"), PortNumber.FromInt(_listenPort), DirectCandidateKind.DirectLan)` if not already present.

### 2.3 Protobuf Schema Specifications (`Percolator.Infrastructure/Protos/`)

#### 1. `unauthenticated_delivery.proto`
Remove `rpc DeleteBlindBlob` and its messages (`BlindBlobDeleteRequestProto`, `BlindBlobDeleteResponseProto`):
```protobuf
service UnauthenticatedDeliveryService {
  rpc DeliverDirect (SealedEnvelopeProto) returns (DeliveryAckProto);
  rpc EnqueueMailbox (SealedEnvelopeProto) returns (EnqueueAckProto);
  rpc FetchPreKeyBundle (FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);
  rpc GetProfile (ProfileFetchRequestProto) returns (ProfileFetchResponseProto);
  rpc UploadProfile (ProfileUploadRequestProto) returns (ProfileUploadResponseProto);
  rpc DeleteProfile (ProfileDeleteRequestProto) returns (ProfileDeleteResponseProto);
  rpc DownloadBlindBlob (BlindBlobDownloadRequestProto) returns (stream BlindBlobChunkProto);
  rpc UploadBlindBlob (stream BlindBlobChunkProto) returns (BlindBlobUploadResponseProto);
}
```

#### 2. `authenticated_relay.proto`
Preserve `bytes authorized_delivery_token = 2;` and add `bytes existing_drain_token = 4;` on `RegisterMailboxRequest`:
```protobuf
message RegisterMailboxRequest {
  bytes routing_token = 1;
  bytes authorized_delivery_token = 2;
  bytes drain_token = 3;
  bytes existing_drain_token = 4; // Optional when drain_token itself matches the existing stored drain_token
}
```

#### 3. `anonymous_group.proto`
Add `rpc CreateGroup` and add `bytes zk_presentation_proof = 4;` to `GroupStreamClientMessage`:
```protobuf
service AnonymousGroupRelayService {
  rpc CreateGroup (CreateGroupRequest) returns (CreateGroupResponse);
  rpc SubscribeGroupStream (GroupStreamClientMessage) returns (stream GroupStreamServerMessage);
  rpc DispatchGroupMessage (DispatchGroupMessageRequest) returns (DispatchGroupMessageResponse);
  rpc CommitRosterMutation (CommitRosterMutationRequest) returns (CommitRosterMutationResponse);
  rpc FetchGroupState (FetchGroupStateRequest) returns (FetchGroupStateResponse);
}

message CreateGroupRequest {
  bytes conversation_id = 1;
  bytes genesis_roster_blob = 2;
  repeated bytes initial_routing_tokens = 3;
  bytes zk_public_params = 4;
}

message CreateGroupResponse {
  bool created = 1;
  uint32 current_epoch = 2;
}

message GroupStreamClientMessage {
  bytes conversation_id = 1;
  uint32 current_epoch = 2;
  bytes subscriber_routing_token = 3;
  bytes zk_presentation_proof = 4;
}
```

#### 4. `discovery.proto`
Add `timestamp_unix_ms`, `signature`, and `identity_key` to `DiscoveryRegistrationProto`:
```protobuf
message DiscoveryRegistrationProto {
  bytes peer_id = 1;
  int32 visibility = 2;
  int64 timestamp_unix_ms = 3;
  bytes signature = 4;    // 64-byte Ed25519 signature over [peer_id (16B) || visibility (4B LE) || timestamp_unix_ms (8B LE)]
  bytes identity_key = 5; // 32-byte Ed25519/IdentityKey public key of peer_id
}
```

---

## 3. Threat Modeling & Security Posture (Lens 3)

| Threat / Vulnerability | Endpoint / Component | Concrete Mitigation |
| :--- | :--- | :--- |
| **Cleartext Sender/Channel Leak to Relay** | `SealedWireFraming` / `EnqueueMailbox` | Encrypt the 53-byte inner metadata header (`Kind`, `ChannelId`, `SenderId`, `SenderDeviceId`, `Reserved`, `PayloadLength`) and payload using AES-256-GCM (`0x53` magic byte + 12B random nonce + 16B tag) so bytes `1..32` are indistinguishable from random noise. |
| **Unauthenticated Mailbox Hijack** | `AuthenticatedRelayEndpoint.RegisterMailbox` | In `IRelayMailboxCapabilityStore.RegisterVerifiedAsync`, if `_mailboxes.TryGetValue(key, out var existing)` succeeds and `existing.DrainToken.Length > 0`, verify `CryptographicOperations.FixedTimeEquals(existing.DrainToken, drainToken.Span)` or (`existingDrainToken.Length == existing.DrainToken.Length && CryptographicOperations.FixedTimeEquals(existing.DrainToken, existingDrainToken.Span)`). If neither matches, return `DomainResult.Failure(new DomainError("MAILBOX_OWNERSHIP_MISMATCH", ...))` and throw `RpcException(StatusCode.PermissionDenied)`. |
| **Unauthenticated Attachment Deletion & Hash Poisoning** | `UnauthenticatedDeliveryEndpoint.UploadBlindBlob` / `DeleteBlindBlob` | Remove `DeleteBlindBlob` completely from `unauthenticated_delivery.proto` and `UnauthenticatedDeliveryEndpoint` (and make `RelayBlobClient.DeleteBlobAsync` return `DomainError("Blob.DeleteUnsupported", ...)`). In `UploadBlindBlob`, compute SHA-256 over `buffer.GetBuffer().AsSpan(0, (int)buffer.Length)` and verify `CryptographicOperations.FixedTimeEquals(actualHash, targetBlobId.Value.Span)` before calling `_chunkStore.SaveBlobAsync`. Throw `StatusCode.InvalidArgument` on mismatch. |
| **Unauthenticated Group Roster & Stream Snooping** | `AnonymousGroupRelayEndpoint.FetchGroupState` & `SubscribeGroupStream` | Add `RelayGroupLedger.VerifyReadPresentation(ZkPresentationBytes proof, IZkProofEngine proofEngine)` which computes deterministic challenge `SHA256(ChannelId [16B] || CurrentEpoch.Value [4B LE])` and calls `proofEngine.VerifyGroupPresentation(CurrentEpoch.Value, proof, challenge, PublicParams)`. Enforce in both `FetchGroupState` and `SubscribeGroupStream`. |
| **Unauthenticated Directory Visibility Spoofing** | `NearbyDiscoveryEndpoint.RegisterVisibility` | Require `IdentityKey` (32B) and `Signature` (64B) on `DiscoveryRegistrationProto`. Verify `Math.Abs((_clock.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(request.TimestampUnixMs)).TotalMinutes) <= 5` (if `request.TimestampUnixMs > 0`), verify `ICryptoEngine.VerifyEd25519Signature(identityKey, payload28, signature)`, and if `IRelayPreKeyRepository.GetAllForIdentityAsync(peerId)` returns any bundle, verify `CryptographicOperations.FixedTimeEquals(bundle.IdentityKey.Span, identityKey.Span)`. |
| **Silent Direct P2P Drop** | `UnauthenticatedDeliveryEndpoint.DeliverDirect` | Inject `IInboundIngressPipeline?` into `UnauthenticatedDeliveryEndpoint`. On `DeliverDirect`, when `_unwrapper != null`, unwrap via `_unwrapper.Unwrap`, check `_ingressFilter.CheckIngressAllowedAsync`, and call `await _ingressPipeline.ProcessInboundAsync(unwrapResult.Value!, context.CancellationToken)`. Return `Success = false` on any unwrap or pipeline failure. |
| **IP Leakage via Default-Enabled P2P File Transfer** | `FileTransferIngressFilter` / `FileTransferActionHandler` | Default `FileTransferIngressFilter.IsEnabled = false` and `SimulatedPersonaHost.AllowFileTransfers = false`. In `FileTransferActionHandler.InitiateTransferAsync` and `AcceptTransferAsync`, if an injected `FileTransferIngressFilter` has `IsEnabled == false`, return `DomainError("FILE_TRANSFER_DISABLED_BY_PRIVACY_POLICY", FileTransferIngressFilter.PrivacyNotice)`. |

---

## 4. High-Value Unit Test & Simulator Scenario Design (Lens 4)

### 4.1 Unit Tests (`Percolator.Infrastructure.Tests` & `Percolator.Apps.FileTransfer.Tests`)
1. **`SealedEnvelopeUnwrapperTests`**:
   - `Encode_EncryptsHeaderMetadata_DoesNotExposeSenderOrChannelIdInCleartext`: Verifies `frame[0] == 0x53`, `frame.Length == 82 + payload.Length`, and neither `channelId` nor `senderId` bytes appear in cleartext in `frame`.
   - `Unwrap_CorruptedWirePayload_ReturnsDomainFailureWithoutThrowing`: Verifies `FRAME_TOO_SHORT` (`< 82` bytes), `UNKNOWN_FRAME_KIND` (`frame[0] != 0x53`), `FRAME_TRUNCATED` (when byte offset 49–50 is mutated, causing AES-GCM authentication failure), and corrupted inner Protobuf handling.
2. **`TransportDispatcherTests`**:
   - Supply non-null test mocks for all 6 required dependencies in `TransportDispatcher`.
   - Verifies `DirectP2P`, `RelayedOneToOne`, and `RelayedGroup` routes encode `job.Payload` into an AEAD-sealed `SealedWireFraming` frame (`0x53` header), resolve endpoints from `IStreamRegistryManagement` / `IPeerMailboxRoutingRepository` / `IChannelRelayResolver` when route endpoints are null, and decode cleanly back to `payload`.
3. **`UnauthenticatedDeliveryEndpointTests`**:
   - `DeliverDirect_ValidFramedEnvelope_InvokesIngressPipelineAndReturnsSuccess`: Verifies `DeliverDirect` unwraps a valid `SealedWireFraming` frame and forwards the `InboundEnvelope` to `IInboundIngressPipeline`.
   - `UploadBlindBlob_MismatchedSha256Digest_ThrowsInvalidArgumentWithoutSaving`: Verifies content-hash enforcement.
4. **`AuthenticatedRelayEndpointTests`**:
   - `RegisterMailbox_ExistingRoutingTokenWithWrongDrainToken_ThrowsPermissionDenied`: Verifies hijack protection while allowing the legitimate owner (matching `DrainToken`) to update their `AuthorizedDeliveryToken`.
   - `ConnectMailboxStream_PushesLiveEnvelopesAndDeletesOnClientAck`: Verifies live push delivery and `MailboxAckProto` (`QueueId`) deletion.
5. **`AnonymousGroupRelayEndpointTests`**:
   - `FetchGroupState_InvalidZkProof_ThrowsPermissionDenied`: Verifies ZK read gating.
   - `CreateGroup_ValidGenesisRequest_PersistsLedgerInRepository`: Verifies gRPC group creation.
6. **`FileTransferNegotiationTests`**:
   - Verifies default-constructed `FileTransferIngressFilter` has `IsEnabled == false` and blocks both inbound offers and outbound `FileTransferActionHandler.InitiateTransferAsync` until opted in.

### 4.2 End-to-End Headless Simulator Scenarios (`Percolator.Simulator/Scenarios/`)
Because `Percolator.Simulator` is verified via its deterministic headless scenario runner (`ScenarioCliRunner`), **all 8 scenarios must pass with zero warnings/errors (`AssertZeroErrors`)**:
1. `01-direct-handshake-and-chat.json`: Direct loopback gRPC PreKey fetch, X3DH handshake, and Double Ratchet chat via production `TransportDispatcher` and `UnauthenticatedDeliveryEndpoint.DeliverDirect`.
2. `02-relayed-stealth-and-chat.json`: Relay mailbox registration, PreKey publication, offline store-and-forward, auto-drain, and relayed chat.
3. `03-prekey-depletion.json`: OPK consumption and depletion enforcement over gRPC.
4. `04-rich-chat-and-virtual-clock.json`: Disappearing messages, reactions, edits, tombstones, read receipts, and virtual clock eviction.
5. `05-advanced-chat-and-profiles.json`: Blinded profile upload (`UploadBlindBlob` with verified SHA-256 + `UploadProfile`), profile key rotation, and mailbox re-registration with matching `DrainToken`.
6. `06-group-chat-and-epochs.json`: Group creation via `CreateGroup` gRPC, ZK-authenticated `SubscribeGroupStream` & `FetchGroupState`, `CommitRosterMutation`, member eviction, and forward secrecy.
7. `07-peer-discovery.json`: Ed25519-signed `RegisterVisibility`, `QueryNearby`, and HMAC-SHA256 blinded rendezvous locator resolution.
8. `08-swarm-file-transfer.json`: Explicit file transfer privacy opt-in, loopback candidate discovery via `NetworkInterfaceEndpointDiscoveryPort`, multi-stream TCP chunk transfer, late downloader injection, and full SHA-256 disk integrity check.

---

## 5. Step-by-Step Implementation Phases (Lens 5: Cross-Layer Alignment)

> [!TIP] Implementation Instructions for AI Execution (Sized for Gemini 3.8 Flash)
> Execute the 8 small, atomic phases below strictly in order. Each phase modifies **at most 3–7 files** and ends with a deterministic `dotnet test` or headless `--replay` verification gate. Do **not** proceed to the next phase until the current phase's verification command passes with 0 errors.

---

### Phase 1: AEAD `SealedWireFraming`, Strict `TransportDispatcher`, and `DeliverDirect` Pipeline

#### Goal
Encrypt the 53-byte `SealedWireFraming` metadata header with AES-256-GCM so `ChannelId` and `SenderId` are never exposed in cleartext, make production `TransportDispatcher` self-sufficient with non-nullable required dependencies (Rule 1), and wire `UnauthenticatedDeliveryEndpoint.DeliverDirect` to `IInboundIngressPipeline`.

#### Files to Modify (6 files)
1. [`Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/SealedEnvelopeUnwrapper.cs)
   - In `SealedWireFraming`:
     - Set `public const byte SealedMagicByte = 0x53;`, `public const int InnerHeaderSize = 53;`, `public const int OuterAeadHeaderSize = 29;`, and `public const int HeaderSize = 82;`.
     - Add `private static readonly byte[] DefaultKeyIkm = SHA256.HashData("Percolator.SealedWireEnvelope.DefaultKey.v1"u8);`.
     - In `Encode(WireFrameKind kind, ChannelId channelId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> packedWireBytes)`:
       - Build a `byte[] plaintext = new byte[InnerHeaderSize + packedWireBytes.Length];` with `plaintext[0] = (byte)kind`, `channelId.TryWriteBytes(plaintext.AsSpan(1, 16))`, `senderId.TryWriteBytes(plaintext.AsSpan(17, 16))`, `BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(33, 4), senderDeviceId.Value)`, `plaintext.AsSpan(37, 12).Clear()`, `BinaryPrimitives.WriteUInt32LittleEndian(plaintext.AsSpan(49, 4), (uint)packedWireBytes.Length)`, and copy `packedWireBytes.Span` to `plaintext.AsSpan(53)`.
       - Allocate `byte[] output = new byte[HeaderSize + packedWireBytes.Length];`, set `output[0] = SealedMagicByte;`, fill `output.AsSpan(1, 12)` via `RandomNumberGenerator.Fill`, derive 32-byte key via `HKDF.DeriveKey(HashAlgorithmName.SHA256, DefaultKeyIkm, 32, output.AsSpan(1, 12), "Percolator.SealedWireFraming.AEAD.v2"u8)`, and encrypt `plaintext` into `output.AsSpan(29)` with tag in `output.AsSpan(13, 16)` and `associatedData: output.AsSpan(0, 1)` using `new AesGcm(derivedKey, 16)`.
     - In `Decode(ReadOnlyMemory<byte> frameBytes)`:
       - If `frameBytes.Length < HeaderSize`, return failure `FRAME_TOO_SHORT`.
       - If `frameBytes.Span[0] != SealedMagicByte`, return failure `UNKNOWN_FRAME_KIND`.
       - Derive key from `nonce = frameBytes.Span.Slice(1, 12)` and decrypt `frameBytes.Span.Slice(29)` using `tag = frameBytes.Span.Slice(13, 16)` and `associatedData = frameBytes.Span.Slice(0, 1)`. Catch `CryptographicException` and return failure `FRAME_TRUNCATED`.
       - Validate inner `kind` (`DirectRatchet`, `GroupBroadcast`, `X3dhHandshake`); if invalid, return failure `UNKNOWN_FRAME_KIND`.
       - Read `channelId`, `senderId`, `deviceVal`, and `payloadLength`; if `InnerHeaderSize + payloadLength > plaintext.Length`, return failure `FRAME_TRUNCATED`.
   - In `SealedEnvelopeUnwrapper.Unwrap(MailboxEnvelope envelope)`:
     - Change line 104 from `if (envelope.Ciphertext.Length > 0 && envelope.Ciphertext.Span[0] > 0x03)` to `if (envelope.Ciphertext.Length > 0 && envelope.Ciphertext.Span[0] != SealedWireFraming.SealedMagicByte)` so `0x53` frames take the fast path directly to `UnwrapFrame`!
2. [`Percolator.Infrastructure/Transport/StreamRegistry.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/StreamRegistry.cs)
   - Add `void RegisterDirectEndpoint(PublicIdentityId targetId, Uri directEndpoint);` and `Uri? GetDirectEndpoint(PublicIdentityId targetId);` to `IStreamRegistryManagement` and implement them in `StreamRegistry` backed by `private readonly ConcurrentDictionary<PublicIdentityId, Uri> _directEndpoints = new();`.
3. [`Percolator.Infrastructure/Transport/TransportDispatcher.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/TransportDispatcher.cs)
   - Declare all required dependencies non-nullable (Anti-Shortcut Rule 1):
     ```csharp
     public TransportDispatcher(
         ITransportClientInvoker invoker,
         IZkProofEngine zkProofEngine,
         IGroupCredentialsRepository groupCredentialsRepository,
         IPeerMailboxRoutingRepository mailboxRoutingRepository,
         IChannelRelayResolver channelRelayResolver,
         IStreamRegistryManagement streamRegistry)
     ```
   - Add helper `public static WireFrameKind DetermineWireFrameKind(ReadOnlyMemory<byte> payload, DeliveryRouteType routeType)`:
     - If `routeType == DeliveryRouteType.RelayedGroup`, return `WireFrameKind.GroupBroadcast`.
     - Try `HandshakeInvitationPackageProto.Parser.ParseFrom(payload.Span)`: if `InitiatorIdentityKey.Length == 32 && InitiatorEphemeralKey.Length == 32`, return `WireFrameKind.X3dhHandshake`.
     - Try `GroupSessionPackageProto.Parser.ParseFrom(payload.Span)`: if `ConversationId.Length == 16 && !Ciphertext.IsEmpty`, return `WireFrameKind.GroupBroadcast`.
     - Return `WireFrameKind.DirectRatchet`.
   - Add helper `private static ReadOnlyMemory<byte> EnsureSealedFrame(OutboxJob job)`: if `job.Payload.Length >= SealedWireFraming.HeaderSize && job.Payload.Span[0] == SealedWireFraming.SealedMagicByte`, return `job.Payload`; otherwise call `SealedWireFraming.Encode(DetermineWireFrameKind(job.Payload, job.Route.Type), job.ChannelId, job.OwnerIdentityId, DeviceId.Primary, job.Payload)`.
   - Use `EnsureSealedFrame(job)` in `DirectP2P`, `RelayedOneToOne`, and `RelayedGroup` (and compute `RelayGroupLedger.ComputeDispatchChallenge(framedPayload.Span, challengeHash)` over the framed payload in `RelayedGroup`).
   - In `DirectP2P`: resolve `var directEndpoint = route.DirectEndpoint ?? (job.RecipientIdentityId.HasValue ? _streamRegistry.GetDirectEndpoint(job.RecipientIdentityId.Value) : null);`.
   - In `RelayedOneToOne`: if `route.RelayEndpoint == null || !route.TargetToken.HasValue || route.TargetDeliveryToken == null`, resolve from `_mailboxRoutingRepository.GetByPeerIdAsync(job.RecipientIdentityId.Value, ct)`.
   - In `RelayedGroup`: if `route.RelayEndpoint == null`, resolve `relayUri = await _channelRelayResolver.ResolveRelayForChannelAsync(job.ChannelId, ct)`.
4. [`Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs)
   - Add optional constructor parameter `IInboundIngressPipeline? ingressPipeline = null` (after `IIngressFilterService? ingressFilter = null` to support relay-only hosting).
   - In `DeliverDirect`:
     - Only execute the unwrap/filter/pipeline block when `_unwrapper is not null` (when `_unwrapper is null` in basic transport connectivity tests, continue returning `new DeliveryAckProto { Success = true }`).
     - When `_unwrapper is not null`: call `var unwrapResult = _unwrapper.UnwrapFrame(request.EncryptedPackage.Memory);`. If `unwrapResult.IsFailure`, return `new DeliveryAckProto { Success = false, ErrorMessage = unwrapResult.Error.Description }`.
     - If `_ingressFilter is not null`, call `var filterResult = await _ingressFilter.CheckIngressAllowedAsync(unwrapResult.Value!, context.CancellationToken);` and return `new DeliveryAckProto { Success = false, ErrorMessage = filterResult.Error.Description }` if `filterResult.IsFailure`.
     - If `_ingressPipeline is not null`, invoke `var ingressResult = await _ingressPipeline.ProcessInboundAsync(unwrapResult.Value!, context.CancellationToken);`. If `ingressResult.IsFailure && ingressResult.Error.Code != "EXPIRED_OR_DUPLICATE_MESSAGE"`, return `new DeliveryAckProto { Success = false, ErrorMessage = ingressResult.Error.Description }`.
5. [`Percolator.Infrastructure.Tests/Transport/Dispatching/SealedEnvelopeUnwrapperTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure.Tests/Transport/Dispatching/SealedEnvelopeUnwrapperTests.cs)
   - Add `Encode_EncryptsHeaderMetadata_DoesNotExposeSenderOrChannelIdInCleartext` asserting that `frame` does not contain `channelId` or `senderId` bytes in cleartext.
6. [`Percolator.Infrastructure.Tests/Transport/Dispatching/TransportDispatcherTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Dispatching/TransportDispatcherTests.cs)
   - Supply non-null test mocks for all 6 dependencies in `TransportDispatcher` test setup.
   - Update assertions on `invoker.LastDirectRequest!.EncryptedPackage` and `invoker.LastGroupRequest!.Ciphertext` to decode via `SealedWireFraming.Decode` and assert `PackedWireBytes.ToArray().Should().Equal(payload)`.

#### Phase 1 Verification Gate
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SealedEnvelopeUnwrapperTests|FullyQualifiedName~TransportDispatcherTests|FullyQualifiedName~UnauthenticatedDeliveryEndpointTests"
```

---

### Phase 2: Production Persistence, Vault & Endpoint Discovery Improvements

#### Goal
Improve `GroupCredentials` / `SqliteGroupCredentialsRepository` to persist `CurrentEpoch`, fix `SqliteRatchetSessionRepository.SaveSessionAsync` to replace superseded sessions on the same `(OwnerIdentityId, OwnerDeviceId, RemotePeerId, RemoteDeviceId)` route, add `VaultStorageOptions.UseEncryption` support to `SqlCipherPragmaInterceptor`, add `allowLoopback` to `NetworkInterfaceEndpointDiscoveryPort`, and register `IChannelRelayResolver` as a singleton in `InfrastructureServiceCollectionExtensions`.

#### Files to Modify (6 files)
1. [`Percolator.Domain/Security/Model/GroupCredentials.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Domain/Security/Model/GroupCredentials.cs) & [`Percolator.Infrastructure/Persistence/Repositories/SqliteGroupCredentialsRepository.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Persistence/Repositories/SqliteGroupCredentialsRepository.cs)
   - In `GroupCredentials`: add `public EpochNumber CurrentEpoch { get; private set; }` (with optional `EpochNumber currentEpoch = default` on constructor and `CreateGenesis` normalizing `currentEpoch.Value == 0 ? EpochNumber.Genesis : currentEpoch`) and `public void UpdateEpoch(EpochNumber newEpoch) => CurrentEpoch = newEpoch;`.
   - In `SqliteGroupCredentialsRepository`: when saving `credentials.CurrentEpoch.Value > 0`, store `AuthCredentialMac` prefixed with `"EPCH"u8` (4B) + `credentials.CurrentEpoch.Value` (4B LE); when loading in `GetAsync`, if `entity.AuthCredentialMac.Length > 8 && entity.AuthCredentialMac.AsSpan(0, 4).SequenceEqual("EPCH"u8)`, extract `CurrentEpoch` and slice `entity.AuthCredentialMac.AsSpan(8)`.
   - In `TransportDispatcher.SendAsync` (`RelayedGroup`): set `epoch = creds.CurrentEpoch.Value` when `creds != null`.
2. [`Percolator.Infrastructure/Persistence/Repositories/SqliteRatchetSessionRepository.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Persistence/Repositories/SqliteRatchetSessionRepository.cs)
   - In `SaveSessionAsync(DirectRatchetSession session, CancellationToken ct)`: before inserting a new `VaultRatchetSessionEntity`, query `_dbContext.VaultRatchetSessions.Where(s => s.OwnerIdentityId == ownerGuid && s.OwnerDeviceId == ownerDevVal && s.RemotePeerId == remoteGuid && s.RemoteDeviceId == remoteDevVal && s.SessionId != sessionIdStr)` and remove any superseded rows so re-handshaking never violates `ix_vault_ratchet_route`.
3. [`Percolator.Infrastructure/Vault/VaultStorageOptions.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Vault/VaultStorageOptions.cs) & [`Percolator.Infrastructure/Vault/SqlCipherPragmaInterceptor.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Vault/SqlCipherPragmaInterceptor.cs)
   - Add `public bool UseEncryption { get; set; } = true;` to `VaultStorageOptions`.
   - Add optional `VaultStorageOptions? options = null` to `SqlCipherPragmaInterceptor(IMasterKeyHandle keyHandle, VaultStorageOptions? options = null)`. When `_options is { UseEncryption: false }`, skip `PRAGMA cipher_memory_security = ON;` and `_keyHandle.ApplyKeyToConnection(sqliteConn)`, and execute only `PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;`.
4. [`Percolator.Infrastructure/Plugins/FileTransfer/NetworkInterfaceEndpointDiscoveryPort.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Plugins/FileTransfer/NetworkInterfaceEndpointDiscoveryPort.cs)
   - Update constructor signature to `public NetworkInterfaceEndpointDiscoveryPort(int listenPort = 50001, string relayBaseUri = "https://relay.percolator.net/pipes", bool allowLoopback = false)` (keeping `50001` as default so `PortNumber.FromInt(_listenPort)` never throws `ArgumentOutOfRangeException`).
   - In `DiscoverLocalCandidatesAsync`: when `_allowLoopback` is true, ensure `new DirectEndpointCandidate(EndpointHost.Parse("127.0.0.1"), PortNumber.FromInt(_listenPort), DirectCandidateKind.DirectLan)` is included in `candidates`.
5. [`Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs)
   - Change line 217 from `services.AddScoped<IChannelRelayResolver, ChannelRelayResolver>();` to `services.AddSingleton<IChannelRelayResolver, ChannelRelayResolver>();`.

#### Phase 2 Verification Gate
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj
```

---

### Phase 3: Blind Blob SHA-256 Verification, Mailbox Ownership Protection, and Live `ConnectMailboxStream`

#### Goal
Remove unauthenticated `DeleteBlindBlob`, enforce SHA-256 content-addressing on `UploadBlindBlob`, protect `RegisterMailbox` against unauthenticated overwrites, and upgrade `ConnectMailboxStream` to push live arrivals and await client `MailboxAckProto` (`QueueId`).

#### Files to Modify (7 files)
1. [`Percolator.Infrastructure/Protos/unauthenticated_delivery.proto`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Protos/unauthenticated_delivery.proto)
   - Remove `rpc DeleteBlindBlob (BlindBlobDeleteRequestProto) returns (BlindBlobDeleteResponseProto);` and messages `BlindBlobDeleteRequestProto` / `BlindBlobDeleteResponseProto`.
2. [`Percolator.Infrastructure/Protos/authenticated_relay.proto`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Protos/authenticated_relay.proto)
   - Add `bytes existing_drain_token = 4;` to `RegisterMailboxRequest` (keeping `bytes authorized_delivery_token = 2;`).
3. [`Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/UnauthenticatedDeliveryEndpoint.cs) & [`Percolator.Infrastructure/Plugins/BlobStorage/RelayBlobClient.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Plugins/BlobStorage/RelayBlobClient.cs)
   - In `UnauthenticatedDeliveryEndpoint`: delete `DeleteBlindBlob`.
   - In `UnauthenticatedDeliveryEndpoint.UploadBlindBlob`: before `await _chunkStore.SaveBlobAsync(targetBlobId.Value, buffer, context.CancellationToken)`, compute `Span<byte> actualHash = stackalloc byte[32]; SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), actualHash);`. If `!CryptographicOperations.FixedTimeEquals(actualHash, targetBlobId.Value.Span)`, throw `new RpcException(new Status(StatusCode.InvalidArgument, "Uploaded blob SHA-256 digest does not match declared BlobId."))`.
   - In `RelayBlobClient.DeleteBlobAsync`: replace the removed `DeleteBlindBlobAsync` gRPC call with `return ValueTask.FromResult(DomainResult.Failure(new DomainError("Blob.DeleteUnsupported", "Remote blind blob deletion is disabled; relay blobs expire automatically via TTL.")));`.
4. [`Percolator.Infrastructure/Transport/IRelayMailboxCapabilityStore.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/IRelayMailboxCapabilityStore.cs) & [`Percolator.Simulator/Features/RelayedChat/SimulatorNotifyingRelayCapabilityStoreDecorator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/SimulatorNotifyingRelayCapabilityStoreDecorator.cs)
   - Add to `IRelayMailboxCapabilityStore` (and implement in `InMemoryRelayMailboxCapabilityStore`):
     - `ValueTask<DomainResult> RegisterVerifiedAsync(ReadOnlyMemory<byte> routingToken, ReadOnlyMemory<byte> deliveryToken, ReadOnlyMemory<byte> drainToken, ReadOnlyMemory<byte> existingDrainToken = default, PublicIdentityId? ownerId = null, DeviceId? deviceId = null, CancellationToken ct = default);`
       - If an entry already exists with a non-empty `DrainToken`, verify `CryptographicOperations.FixedTimeEquals(entry.DrainToken, drainToken.Span)` OR (`existingDrainToken.Length == entry.DrainToken.Length && CryptographicOperations.FixedTimeEquals(entry.DrainToken, existingDrainToken.Span)`). If false, return `DomainResult.Failure(new DomainError("MAILBOX_OWNERSHIP_MISMATCH", "Cannot overwrite existing mailbox without valid drain token."))`.
     - `IDisposable SubscribeMailbox(ReadOnlyMemory<byte> routingToken, ChannelWriter<QueuedSealedEnvelopeProto> writer);`
     - `ValueTask AcknowledgeEnvelopeAsync(ReadOnlyMemory<byte> routingToken, string queueId, CancellationToken ct = default);`
   - Expose queue inspection/chaos helpers on `InMemoryRelayMailboxCapabilityStore` and refactor `SimulatorNotifyingRelayCapabilityStoreDecorator` to wrap an inner `InMemoryRelayMailboxCapabilityStore` instance instead of duplicating its mailbox storage logic.
5. [`Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/AuthenticatedRelayEndpoint.cs)
   - In `RegisterMailbox`: call `var regResult = await _capabilityStore.RegisterVerifiedAsync(request.RoutingToken.Memory, request.AuthorizedDeliveryToken.Memory, request.DrainToken.Memory, request.ExistingDrainToken.Memory, ct: context.CancellationToken);`. If `regResult.IsFailure`, throw `new RpcException(new Status(StatusCode.PermissionDenied, regResult.Error.Description))`.
   - In `ConnectMailboxStream`: after sending `AuthResultProto { Authenticated = true }`, subscribe a `Channel.CreateUnbounded<QueuedSealedEnvelopeProto>()` via `_capabilityStore.SubscribeMailbox(authResp.RoutingToken.Memory, channel.Writer)`. Drain initial queued envelopes to `responseStream`, then loop reading client messages (`MailboxAckProto` -> `_capabilityStore.AcknowledgeEnvelopeAsync(authResp.RoutingToken.Memory, ack.QueueId, context.CancellationToken)`) and forwarding live `channel.Reader` items until the client closes `requestStream` or `context.CancellationToken` fires.
6. [`Percolator.Infrastructure.Tests/Plugins/BlobStorage/RelayBlobClientTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure.Tests/Plugins/BlobStorage/RelayBlobClientTests.cs), [`Percolator.Infrastructure.Tests/Transport/Endpoints/UnauthenticatedDeliveryEndpointTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure.Tests/Transport/Endpoints/UnauthenticatedDeliveryEndpointTests.cs) & [`AuthenticatedRelayEndpointTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure.Tests/Transport/Endpoints/AuthenticatedRelayEndpointTests.cs)
   - In `RelayBlobClientTests`: remove `DirectEndpointClient.DeleteBlindBlobAsync` and assert that `_directClient.DeleteBlobAsync` returns `Blob.DeleteUnsupported`.
   - In `UnauthenticatedDeliveryEndpointTests` & `AuthenticatedRelayEndpointTests`: add tests for `UploadBlindBlob` SHA-256 mismatch rejection and `RegisterMailbox` ownership hijack rejection.

#### Phase 3 Verification Gate
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~RelayBlobClientTests|FullyQualifiedName~UnauthenticatedDeliveryEndpointTests|FullyQualifiedName~AuthenticatedRelayEndpointTests"
```

---

### Phase 4: Group Relay ZK Read Verification, gRPC `CreateGroup`, and Signed `RegisterVisibility`

#### Goal
Enforce ZK presentation proof verification on `FetchGroupState` and `SubscribeGroupStream`, add `CreateGroup` to `AnonymousGroupRelayService`, and require Ed25519 signature verification on `NearbyDiscoveryEndpoint.RegisterVisibility`.

#### Files to Modify (6 files)
1. [`Percolator.Domain/Delivery/Hosting/RelayGroupLedger.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Domain/Delivery/Hosting/RelayGroupLedger.cs)
   - Add `public static void ComputeReadChallenge(ChannelId channelId, EpochNumber epoch, Span<byte> destination)`:
     ```csharp
     Span<byte> buffer = stackalloc byte[20];
     channelId.TryWriteBytes(buffer[..16]);
     BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..20], epoch.Value);
     SHA256.HashData(buffer, destination);
     ```
   - Add `public DomainResult VerifyReadPresentation(ZkPresentationBytes proof, IZkProofEngine proofEngine)`:
     - Compute `Span<byte> challenge = stackalloc byte[32]; ComputeReadChallenge(ChannelId, CurrentEpoch, challenge);`.
     - If `!proofEngine.VerifyGroupPresentation(CurrentEpoch.Value, proof, challenge, PublicParams)`, return `DomainResult.Failure(new DomainError("INVALID_ZK_PROOF", "The ZK membership presentation proof is invalid for group state read."))`.
     - Return `DomainResult.Success()`.
2. [`Percolator.Infrastructure/Protos/anonymous_group.proto`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Protos/anonymous_group.proto) & [`Percolator.Infrastructure/Transport/Endpoints/AnonymousGroupRelayEndpoint.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Transport/Endpoints/AnonymousGroupRelayEndpoint.cs)
   - Add `rpc CreateGroup (CreateGroupRequest) returns (CreateGroupResponse);` and `bytes zk_presentation_proof = 4;` on `GroupStreamClientMessage`.
   - Implement `CreateGroup` in `AnonymousGroupRelayEndpoint`: parse `ChannelId`, `EncryptedEntriesBlob`, `HashSet<BlindedRoutingToken>`, and `ZkGroupPublicParams`, call `RelayGroupLedger.CreateGenesis(channelId, PublicIdentityId.New(), genesisBlob, tokens, publicParams, _timeProvider)`, save via `_ledgerRepository.SaveAsync`, and return `CreateGroupResponse { Created = true, CurrentEpoch = ledger.CurrentEpoch.Value }`.
   - In `FetchGroupState` and `SubscribeGroupStream`: validate `request.ZkPresentationProof.Length >= 64`, construct `ZkPresentationBytes.FromSpan(request.ZkPresentationProof.Span)`, call `ledger.VerifyReadPresentation(proof, _zkProofEngine)`, and throw `new RpcException(new Status(StatusCode.PermissionDenied, "INVALID_ZK_PROOF"))` on failure.
3. [`Percolator.Infrastructure/Protos/discovery.proto`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Protos/discovery.proto) & [`Percolator.Infrastructure/Plugins/Discovery/NearbyDiscoveryEndpoint.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Plugins/Discovery/NearbyDiscoveryEndpoint.cs)
   - Add `int64 timestamp_unix_ms = 3;`, `bytes signature = 4;`, and `bytes identity_key = 5;` to `DiscoveryRegistrationProto`.
   - Add `public static byte[] BuildVisibilitySignaturePayload(PublicIdentityId peerId, int visibility, long timestampUnixMs)` returning the 28-byte buffer `[peerId (16B) || visibility (4B LE) || timestampUnixMs (8B LE)]`.
   - Add optional `ICryptoEngine? cryptoEngine = null` to `NearbyDiscoveryEndpoint` constructor (and pass `cryptoEngine` in [`SimulatedRelayHost.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs#L210)).
   - In `RegisterVisibility`: when `_cryptoEngine is not null`, require `request.IdentityKey.Length == 32 && request.Signature.Length == 64`, verify timestamp is within $\pm 5$ minutes of `_clock.UtcNow`, verify `_cryptoEngine.VerifyEd25519Signature(IdentityKey.FromSpan(request.IdentityKey.Span), payload, request.Signature.Span)`, and if `_preKeyRepo` has an existing bundle for `peerId`, verify `CryptographicOperations.FixedTimeEquals(bundle.IdentityKey.Span, request.IdentityKey.Span)`. Throw `RpcException(StatusCode.PermissionDenied)` if invalid.
4. [`Percolator.Simulator/Features/DiscoverySimulation/DiscoverySimulationCoordinator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/DiscoverySimulation/DiscoverySimulationCoordinator.cs) & [`Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/RelayProvisioningCoordinator.cs)
   - Sign `DiscoveryRegistrationProto` in both `DiscoverySimulationCoordinator.SetDiscoveryVisibilityAsync` and `RelayProvisioningCoordinator.RegisterMailboxAsync` using `persona.IdentityPrivateKey`, `persona.IdentityPublicKey`, and `NearbyDiscoveryEndpoint.BuildVisibilitySignaturePayload`.
5. [`Percolator.Infrastructure.Tests/Transport/Endpoints/AnonymousGroupRelayEndpointTests.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure.Tests/Transport/Endpoints/AnonymousGroupRelayEndpointTests.cs)
   - Populate valid `ZkPresentationProof` on `GroupStreamClientMessage` in existing tests and add tests for `FetchGroupState` ZK proof verification and `CreateGroup`.

#### Phase 4 Verification Gate
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj --filter "FullyQualifiedName~AnonymousGroupRelayEndpointTests|FullyQualifiedName~Discovery"
```

---

### Phase 5: File Transfer Opt-In Privacy Default (`Percolator.Apps.FileTransfer`)

#### Goal
Default the P2P file transfer feature to **disabled** (`IsEnabled = false`), register concrete `FileTransferIngressFilter` in DI, and enforce the opt-in check on both outbound transfer initiation/acceptance (`FileTransferActionHandler`) and inbound offer reception (`FileTransferIngressFilter`).

#### Files to Modify (4 files)
1. [`Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferIngressFilter.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/IngressControl/FileTransferIngressFilter.cs)
   - Set `public bool IsEnabled { get; set; } = false;` by default.
   - Add `public const string PrivacyNotice = "Direct P2P file transfer uses multi-stream TCP connections that expose your IP address to the peer and is significantly less private than relayed chat.";`.
2. [`Percolator.Apps.FileTransfer/DependencyInjection/ServiceCollectionExtensions.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/DependencyInjection/ServiceCollectionExtensions.cs)
   - Update `AddFileTransferApp` to register concrete `FileTransferIngressFilter` as a singleton and forward `IAppIngressFilter` to the same singleton instance:
     ```csharp
     collection.AddSingleton<FileTransferIngressFilter>();
     collection.AddSingleton<IAppIngressFilter>(sp => sp.GetRequiredService<FileTransferIngressFilter>());
     ```
3. [`Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer/Features/TransferNegotiation/FileTransferActionHandler.cs)
   - Add constructor parameter `FileTransferIngressFilter? ingressFilter = null` (Anti-Shortcut Rule 9: directly inject filter, never `IServiceProvider`).
   - At the start of `InitiateTransferAsync` and `AcceptTransferAsync`, if `_ingressFilter is { IsEnabled: false }`, return failure `new DomainError("FILE_TRANSFER_DISABLED_BY_PRIVACY_POLICY", FileTransferIngressFilter.PrivacyNotice)`.
4. [`Percolator.Apps.FileTransfer.Tests`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.FileTransfer.Tests)
   - Update existing `FileTransferIngressFilter` test setups to explicitly opt in (`IsEnabled = true`) when testing enabled flows, and add a unit test verifying that default-constructed `FileTransferIngressFilter` and `FileTransferActionHandler` reject transfers until enabled.

#### Phase 5 Verification Gate
```powershell
dotnet test Percolator.Apps.FileTransfer.Tests/Percolator.Apps.FileTransfer.Tests.csproj
```

---

### Phase 6: Delete Redundant Simulator Infrastructure & Use Real Production Classes (`Percolator.Simulator`)

#### Goal
Delete `SimulatorDirectIngressBridge`, `SimulatorTransportDispatcher`, `SimulatorSqlCipherPragmaInterceptor`, `SimulatorRatchetSessionRepositoryDecorator`, and `RelayPreKeyAvailabilityAdapter`; wire `SimulatedPersonaHost` and `SimulatedRelayHost` to use the real production classes directly; and fetch direct PreKey bundles over loopback gRPC in `DirectHandshakeOrchestrator`.

#### Files to Modify / Delete (6 files)
1. **Delete Redundant Simulator Workaround Files**:
   - Delete [`Percolator.Simulator/Features/Topology/SimulatorDirectIngressBridge.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorDirectIngressBridge.cs).
   - Delete [`Percolator.Simulator/Features/Topology/SimulatorTransportDispatcher.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorTransportDispatcher.cs).
   - Delete [`Percolator.Simulator/Features/Topology/SimulatorSqlCipherPragmaInterceptor.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorSqlCipherPragmaInterceptor.cs).
2. [`Percolator.Simulator/Features/Topology/SimulatorNotifyingHandshakeDecorator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatorNotifyingHandshakeDecorator.cs)
   - Delete `SimulatorRatchetSessionRepositoryDecorator` (leaving only `SimulatorNotifyingHandshakeDecorator` for UI state-bus notifications).
3. [`Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/Topology/SimulatedPersonaHost.cs)
   - Set `UseEncryption = false` on `VaultStorageOptions` (using production `SqlCipherPragmaInterceptor`).
   - Register `services.AddSingleton<INetworkEndpointDiscoveryPort>(_ => new NetworkInterfaceEndpointDiscoveryPort(BoundPort, allowLoopback: true));`.
   - Change `private bool _allowFileTransfers = false;` and in the `AllowFileTransfers` property setter, update `_host?.Services.GetService<FileTransferIngressFilter>()` (`filter.IsEnabled = value`) to ensure Autopilot Host Parity (Rule 5).
   - Use production `TransportDispatcher`, `IngressFilterService`, `SqliteRatchetSessionRepository`, and `UnauthenticatedDeliveryEndpoint` (wired with `IInboundIngressPipeline` decorated only to flush `OutboxWorker.ProcessBatchAsync(50, ct)` after successful inbound processing so automatic receipts/replies are dispatched immediately).
   - Seed the persona's own `RelayHostedPreKeyBundle` into its local `IRelayPreKeyRepository` and register its `MailboxRoutingToken` / `MailboxDeliveryToken` / `MailboxDrainToken` in its local `IRelayMailboxCapabilityStore` during `StartAsync` so direct peers can call `FetchPreKeyBundle` over loopback gRPC.
4. [`Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/DirectChat/DirectHandshakeOrchestrator.cs)
   - In `InitiateDirectHandshakeAsync` and `AcceptHandshakeAsync`: register `target.BoundEndpoint` and `initiator.BoundEndpoint` in each peer's `IStreamRegistryManagement.RegisterDirectEndpoint` so production `TransportDispatcher` resolves `DirectP2P` endpoints without `_worldState`.
   - In `InitiateDirectHandshakeAsync`: replace `var targetBundle = target.CreatePreKeyBundle();` with a loopback gRPC call to `UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient.FetchPreKeyBundleAsync` on `target.BoundEndpoint` using `target.MailboxRoutingToken` and `target.MailboxDeliveryToken` (falling back to `NoOpk` if OPKs are depleted).

#### Phase 6 Verification Gate (Scenarios 01–05)
```powershell
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/01-direct-handshake-and-chat.json
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/02-relayed-stealth-and-chat.json
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/03-prekey-depletion.json
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/04-rich-chat-and-virtual-clock.json
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/05-advanced-chat-and-profiles.json
```

---

### Phase 7: Simulator Group gRPC Provisioning, Elimination of `_groupRelays` Backdoor & ZK Read Proofs (`Percolator.Simulator`)

#### Goal
Replace in-memory `relay.ProvisionGroupLedgerAsync(...)` with `AnonymousGroupRelayServiceClient.CreateGroupAsync` over gRPC, delete coordinator side-channel `_groupRelays` dictionary (Anti-Shortcut Rule 14), register group relay endpoints in each persona's local `IChannelRelayResolver` and `GroupCredentials` (with `CurrentEpoch`) in `IGroupCredentialsRepository` so production `TransportDispatcher` dispatches group broadcasts with zero `_worldState` access, and delete `RelayPreKeyAvailabilityAdapter` from `SimulatedRelayHost`.

#### Files to Modify (3 files)
1. [`Percolator.Simulator/Features/GroupMessaging/GroupChatCoordinator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/GroupMessaging/GroupChatCoordinator.cs)
   - Delete the `_groupRelays` side-channel dictionary (`ConcurrentDictionary<ChannelId, Uri> _groupRelays`).
   - In `CreateGroupOnRelayAsync` and `AcceptGroupInviteAsync`:
     - Register `(channelId, relayUri)` directly into each member's own local `IChannelRelayResolver.RegisterRelayForChannel`.
     - Construct `GroupCredentials` via `GroupCredentials.CreateGenesis(channelId, GroupMasterKey.FromSpan(new byte[32]), AuthCredentialMacBytes.FromSpan(new byte[64]), currentEpoch: EpochNumber.Genesis)` and save in `IGroupCredentialsRepository` so production `TransportDispatcher` resolves both the relay URI and `CurrentEpoch` from the persona's own DI/SQLite state.
     - Replace `await relay.ProvisionGroupLedgerAsync(ledgerResult.Value!, ct);` with a gRPC call to `AnonymousGroupRelayService.AnonymousGroupRelayServiceClient.CreateGroupAsync` on `relay.BoundEndpoint`.
   - When `CommitRosterMutationAsync` succeeds or `GroupStreamServerMessage.PayloadOneofCase.Mutation` is received on the group stream, update `GroupCredentials.UpdateEpoch(new EpochNumber(newEpoch))` in the persona's `IGroupCredentialsRepository`.
   - In `CommitRosterMutationAsync`, `GetGroupDetailsAsync`, and `SubscribePersonaToGroup`:
     - Generate a ZK read presentation proof via `IZkProofEngine.GenerateGroupPresentation(epoch, challenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty)` (where `challenge` is computed via `RelayGroupLedger.ComputeReadChallenge(channelId, new EpochNumber(epoch), challenge)`) and set `ZkPresentationProof = ByteString.CopyFrom(proof.Span)` on `FetchGroupStateRequest` and `GroupStreamClientMessage`.
2. [`Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/SimulatedRelayHost.cs) & [`ISimulatedRelayHost.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/RelayedChat/ISimulatedRelayHost.cs)
   - Set `UseEncryption = false` on `VaultStorageOptions` and use production `SqlCipherPragmaInterceptor`.
   - Delete private `RelayPreKeyAvailabilityAdapter` and register production `Percolator.Infrastructure.Adapters.PreKeyAvailabilityAdapter` for `IPreKeyAvailabilityPort`.
   - Remove the unused `ProvisionGroupLedgerAsync` method from `ISimulatedRelayHost` and `SimulatedRelayHost`.

#### Phase 7 Verification Gate (Scenarios 06 & 07)
```powershell
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/06-group-chat-and-epochs.json
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios/07-peer-discovery.json
```

---

### Phase 8: Simulator Swarm File Transfer Opt-In, Loopback Discovery & Full 8-Scenario Gate

#### Goal
Update `SwarmSimulationCoordinator` to explicitly opt into the file transfer privacy gate without cross-host RAM peeking (Anti-Shortcut Rule 2), verify loopback endpoint candidate discovery via `INetworkEndpointDiscoveryPort`, enforce `FileTransferIngressFilter.CheckIngressAllowedAsync` on recipient arrival, and dispatch in-band `FileTransferActionHandler.InitiateTransferAsync` when a 1:1 Double Ratchet session exists.

#### Files to Modify (2 files)
1. [`Percolator.Simulator/Features/SwarmSimulation/SwarmSimulationCoordinator.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/SwarmSimulation/SwarmSimulationCoordinator.cs)
   - In `InitiateTransferAsync`:
     - Enable `initiator.AllowFileTransfers = true` and log `FileTransferIngressFilter.PrivacyNotice` to `ISimulatorLogSink`.
     - Resolve `INetworkEndpointDiscoveryPort` from `initiator.Services` and call `await endpointDiscovery.DiscoverLocalCandidatesAsync(DirectWanPolicy.DisabledByDefault, ct)` to verify and log the discovered loopback candidate (`127.0.0.1:{initiator.BoundPort}`).
     - Reject the lazy cross-host RAM peeking shortcut (`r.Services.CreateScope()`): `initiator` does **not** peek into `r.Services`. Instead, `initiator` publishes the offer signal to `_stateBus`, and when `r` receives the offer (or when `AcceptTransferAsync(r.Id, sessionId)` is invoked), `r` enforces its own local `AllowFileTransfers` / `FileTransferIngressFilter.IsEnabled` flag.
     - If an active 1:1 `DirectRatchetSession` exists between `initiator` and `r`, invoke `FileTransferActionHandler.InitiateTransferAsync` inside `initiator.Services.CreateScope()` and flush `initiator.FlushOutboxAsync(ct)` (Anti-Shortcut Rule 11 - Outbox Durability) so the in-band `FileTransferOfferPayload` travels over the Double Ratchet channel.
   - In `AcceptTransferAsync`:
     - Recipient `recipient` sets `recipient.AllowFileTransfers = true` (or validates `recipient.AllowFileTransfers`), invokes `FileTransferActionHandler.AcceptTransferAsync` in its own scope, and flushes `recipient.FlushOutboxAsync(ct)` before commencing multi-stream chunk downloads.
2. [`Percolator.Simulator/Features/SwarmSimulation/SwarmPanelView.axaml`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Simulator/Features/SwarmSimulation/SwarmPanelView.axaml)
   - Add a concise visual privacy warning banner noting that P2P file transfer uses direct multi-stream TCP and defaults to disabled unless opted in.

#### Phase 8 Verification Gate (Full Test Suite & All 8 Simulator Scenarios)
```powershell
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj
dotnet test Percolator.Apps.FileTransfer.Tests/Percolator.Apps.FileTransfer.Tests.csproj
dotnet run --project Percolator.Simulator/Percolator.Simulator.csproj -f net10.0-windows10.0.19041.0 -- --replay Percolator.Simulator/Scenarios
```
