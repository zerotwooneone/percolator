# Percolator.Infrastructure2 Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Infrastructure2` provides concrete technical adapters, storage repositories, network transport clients/servers, cryptographic primitives, and binary serialization engines for the Percolator microkernel architecture.

Following Clean Architecture principles:
- **Target Framework**: Targets standard `net10.0` with **zero Windows-specific or platform-locked dependencies**. Runs uniformly on Linux Docker containers (relay hosts), macOS, and cross-platform desktop runtimes.
- **Dependencies Flow Inward**: `Infrastructure2` references `Percolator.Domain`, `Percolator.Application`, and `Percolator.PluginSdk`. It does NOT expose infrastructure-specific types (e.g., SQLite connections, gRPC stubs, Protobuf classes, raw sockets) to the domain or application layers.
- **Port Realization & Swappability**: Every component in this project implements an interface (port) defined by `Percolator.Domain`, `Percolator.Application`, `Percolator.PluginSdk`, or application plugins (`Percolator.Apps.*`). Platform-specific optimizations (such as Windows DPAPI, Windows SChannel PFX persistence, or NTFS sparse files) are decoupled into `Percolator.Infrastructure.Windows` and swapped in via dependency injection.
- **Wire Contract & Serialization Ownership**: Concrete Protobuf `.proto` schemas, code-generated message classes, gRPC service stubs, and binary serializers live strictly within `Percolator.Infrastructure2`. The inner layers interact solely via pure C# DTOs and domain models via the `IPayloadSerializer` and `ISessionWirePacker` ports.

---

## 2. Legacy Migration & Cutover Strategy

The legacy codebase contains dated, overlapping modules marked for total deletion during the cutover:
- `Percolator.Contracts` (Legacy Protobuf definitions and gRPC service contracts)
- `Percolator.Identity`
- `Percolator.Cryptography`
- `Percolator.Network`
- `Percolator.Infrastructure`
- `Percolator.Wpf` (Legacy Windows-only desktop client)

### Cutover Workflow
1. **Domain & Application Completion**: Implement and verify `Percolator.Domain`, `Percolator.Application`, application plugins (`Percolator.Apps.Chat`, `Discovery`, `FileTransfer`), and their test suites.
2. **Platform Separation**: Core cross-platform infrastructure lives in `Percolator.Infrastructure2`. Dedicated Windows-specific adapters live in `Percolator.Infrastructure.Windows`.
3. **Legacy Project Deletion**: Remove `Percolator.Contracts` and all legacy projects from the solution (`Percolator.sln`) and delete their filesystem directories.
   - None of the modern projects (`Percolator.Domain`, `Percolator.PluginSdk`, `Percolator.Application`, `Percolator.Apps.*`) reference `Percolator.Contracts`.
   - All replacement Protobuf schemas are authored fresh within `Percolator.Infrastructure2/Protos/`.
4. **Compiler-Error-Driven Triage**:
   - The resulting compiler breaks in the desktop app and infrastructure serve as an intentional audit trail.
   - For every breaking symbol/class, decide deliberately:
     - **Keep & Convert**: If an existing adapter (e.g., SQLCipher schema setup or gRPC proto definitions) can be refactored to implement the clean domain/application ports.
     - **Delete & Rewrite**: If the legacy class contains domain leakage, state coupling, or obsolete patterns.

---

## 3. High-Level Requirements

1. **Zero Domain Logic in Infrastructure**: Infrastructure adapters must solely translate between external APIs/data formats and domain value objects / entities.
2. **Zero Plaintext Sensitive State at Rest**: All private keys, ratchet chain keys, and session secrets stored on disk must be encrypted using SQLCipher and/or master secret key derivation. Sensitive cryptographic fields must never be stored as unencrypted BLOBs in SQLite.
3. **Deterministic Memory Zeroization**: Any unmanaged buffers or cryptographic key spans must be cleared (`CryptographicOperations.ZeroMemory`) when disposed.
4. **Resilient Network Handling**: All gRPC network calls must obey cancellation tokens, transport timeouts, and surface transient vs. permanent network failures cleanly to `OutboxRetryPolicy`.
5. **Cryptographic Logging Guardrails**: Honor `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default across all infrastructure loggers and diagnostics. Sensitive cryptographic keys, KDF digests, and plaintexts must never be emitted to logs.
6. **Strict Authentication & Metadata Isolation**:
   - **Public Edge / Ingress & Deposit (Sealed Sender & Capability Ingress)**: Must be unauthenticated (zero sender identity or channel ID leakage on the wire). Relies on recipient-issued `DeliveryToken`s, `ProfileKey`-derived blinded locators, and Sealed Sender cryptography. Relays see strictly routing tokens and blinded capability locators.
   - **Mailbox Retrieval, Directory & Management**: Must be strictly authenticated via challenge-response or signature cryptographic proofs to prevent unauthorized message draining, OPK exhaustion attacks, and mass directory scraping (mirroring Signal's security model).
   - **Group Operations**: Must be protected via Zero-Knowledge presentation proofs against `RelayGroupLedger` epochs without revealing individual member identities to relays.
7. **Zero Platform Lock-in (Cross-Platform Architecture)**: Core infrastructure contracts, cryptographic flows, and persistence abstractions must run seamlessly across Windows, Linux, and macOS. Windows-only optimizations live behind clean ports and are supplied by `Percolator.Infrastructure.Windows`.

---

## 4. Required Port Implementations & Specific Adapters

### 4.1 Cryptography (`Percolator.Domain.Security.Ports.ICryptoEngine` & `IZkProofEngine`)
- **Adapter**: `SignalCryptoEngine` / `SodiumCryptoEngine` (`ICryptoEngine`)
  - **Ed25519 Signatures**: Signs and verifies device link proofs, pre-key signatures, and group sender key messages (`VerifyEd25519Signature(IdentityKey, ...)`, `SignEd25519(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)`).
  - **Curve25519 (X25519) Diffie-Hellman**: Computes scalar multiplication between private keys and public DH keys (`ComputeDiffieHellman`, `DeriveX3dhMasterSecret`).
  - **HKDF / HMAC Ratchet Steps**: Performs SHA-256 HMAC-based root key updates (`KdfRk`) and symmetric chain key advances (`StepRatchet`).
  - **Authenticated Encryption**: AES-256-GCM authenticated symmetric encryption (`EncryptAesGcm`, `DecryptAesGcm`).
  - **Resilient Public Key Parsing**: Supports importing public keys from both raw 32/64/65-byte point spans and ASN.1 DER SubjectPublicKeyInfo structures without throwing parsing exceptions.
  - **Logging Guardrails**: Redacts all key spans and secrets in logs, controlled via `CryptographyOptions`.
  - **RFC 4122 UUID Endianness Normalization**:
    - When bridging between .NET `Guid` structures and native cryptographic libraries (`libsignal`, `zkgroup`) or cross-platform network payloads, explicitly normalize byte orders via `Guid.ToByteArray(bigEndian: true)`.
    - Defends against .NET Windows little-endian byte ordering of Data1, Data2, and Data3 which otherwise corrupts UUIDs passed into native C/Rust functions.
  - **Unmanaged Native Callback Database Threading (`IDbContextFactory<T>`)**:
    - Native C/Rust libraries invoke C-ABI callbacks synchronously (e.g. `LoadSenderKey`, `StoreSenderKey`) on foreign unmanaged threads outside ASP.NET Core request scopes.
    - Interop bridge adapters must consume `IDbContextFactory<T>` to spin up short-lived isolated database contexts, preventing concurrency clashes and cross-thread context contamination.
- **Adapter**: `ZkgroupCryptographyService` (`IZkProofEngine`)
  - **Native zkgroup FFI**: Wraps `Signal.Interop` native C/Rust binaries with safe handle management (`using var ...` wrapping native `SafeHandle`).
  - **ZK Group Presentations**: Generates and verifies zero-knowledge membership proofs for anonymous group relay interactions (`VerifyGroupPresentation`, `GenerateGroupPresentation`).

### 4.2 Serialization, Wire Contracts & Protobuf Schemas (`Protos/`)

Rather than blindly cloning legacy contracts, `Infrastructure2` defines purpose-built Protobuf services organized strictly by **trust boundary** and **authentication model**:

```
                                  ┌────────────────────────────────────────┐
                                  │      Client / Sender / Peer Node       │
                                  └───────────────────┬────────────────────┘
                                      │                   │              │
                   (Unauthenticated / │       (Auth via   │   (Auth via  │
                    Capability Token) │        Signature) │    ZK-Proof) │
                                      ▼                   ▼              ▼
┌──────────────────────────────────────┐ ┌──────────────────┐ ┌─────────────────────┐
│    UnauthenticatedDeliveryService    │ │ Authenticated    │ │ AnonymousGroupRelay │
├──────────────────────────────────────┤ │ RelayService     │ │ Service             │
│ • DeliverDirect(SealedEnvelope)      │ ├──────────────────┤ ├─────────────────────┤
│ • EnqueueMailbox(SealedEnvelope)     │ │ • ConnectMailbox │ │ • SubscribeGroup    │
│ • GetProfile(ProfileFetch)           │ │   Stream()       │ │   Stream()          │
│ • UploadProfile(ProfileUpload)       │ │ • DrainMailbox() │ │ • DispatchGroupMsg()│
│ • DeleteProfile(ProfileDelete)       │ │ • RegisterMbox() │ │ • CommitMutation()  │
│ • DownloadBlindBlob(BlindBlobReq)    │ │ • PublishPreKey()│ │ • FetchGroupState() │
│                                      │ │ • FetchPreKey()  │ └─────────────────────┘
└──────────────────────────────────────┘ └──────────────────┘
```

#### 1. `unauthenticated_delivery.proto` (Public Edge / Sealed Sender & Capability Ingress)
- **Trust Boundary & Sealed Sender Invariant**: Open to peers, anonymous senders, and capability token holders. **Zero sender identity metadata** and **zero channel identifiers** are exposed to transport headers, outer envelopes, or relay logs. The relay only observes routing tokens, blinded capability locators, or content hashes:
  - **Envelope Ingress**: Senders provide `recipient_routing_token` and `delivery_token`. Sender identity and ChannelId live strictly inside the encrypted package (`DirectSessionPackageProto` / `GroupSessionPackageProto` under Double Ratchet E2EE). Protected by rate limiting and recipient-issued `DeliveryToken`s.
  - **Unauthenticated Profile Capability Ingress**: Peers fetch encrypted profile packages using unauthenticated capability lookups (`GetProfile(ProfileFetchRequestProto)`) presenting strictly a 32-byte `BlindedLocator` ($\text{HKDF-SHA256}(\text{IK}_{\text{Owner}}, \text{ProfileKey}, \text{"PercolatorProfileLocatorV2"}, 32)$). No caller identity, auth ticket, or session signature is provided or logged, preventing the relay from building a social graph of who views whose profile. Profile updates (`UploadProfile(ProfileUploadRequestProto)`) and deletions (`DeleteProfile(ProfileDeleteRequestProto)`) require presenting strictly a 32-byte `WriteToken` ($\text{HKDF-SHA256}(\text{IK}_{\text{Owner}}, \text{ProfileKey}, \text{"PercolatorProfileWriteTokenV1"}, 32)$) proving possession of the write authorization secret without identifying the owner to the relay.
  - **Unauthenticated Blind Blob Retrieval**: Avatars and media blobs stored under `StorageMode.PublicRelayBlob` are retrieved out-of-band via unauthenticated capability streaming (`DownloadBlindBlob(BlindBlobDownloadRequestProto)`) referencing strictly the content-addressed `blob_id` without presenting caller identity or `ChannelId`.
- **Messages**:
  - `SealedEnvelopeProto`:
    - `bytes recipient_routing_token = 1;` (16-byte `BlindedRoutingToken` for mailbox routing)
    - `bytes delivery_token = 2;` (32-byte `DeliveryToken` verifying sender authorization to drop message)
    - `bytes encrypted_package = 3;` (Opaque encrypted package containing sender ID, ratchet header, and inner payload)
    - `google.protobuf.Timestamp expires_at_utc = 4;` (Enforces queue purge TTL and limits replay windows)
  - `DeliveryAckProto`: `bool success = 1;`, `string error_message = 2;`
  - `EnqueueAckProto`: `bool accepted = 1;`, `google.protobuf.Timestamp expires_at_utc = 2;`
  - `ProfileFetchRequestProto`:
    - `bytes blinded_locator = 1;` (32-byte `BlindedLocator`)
  - `ProfileFetchResponseProto`:
    - `bool found = 1;`
    - `bytes nonce = 2;` (12-byte AES-GCM nonce)
    - `bytes ciphertext = 3;` (Fixed 1040-byte padded ciphertext block: 1024 B payload + 16 B tag)
    - `uint32 revision = 4;` (Monotonic slot revision)
    - `google.protobuf.Timestamp expires_at_utc = 5;`
  - `ProfileUploadRequestProto`:
    - `bytes blinded_locator = 1;` (32-byte `BlindedLocator`)
    - `bytes write_token = 2;` (32-byte `WriteToken` capability)
    - `uint32 revision = 3;` (Monotonic slot revision)
    - `bytes nonce = 4;` (12-byte AES-GCM nonce)
    - `bytes ciphertext = 5;` (Fixed 1040-byte padded ciphertext block: 1024 B payload + 16 B tag)
    - `google.protobuf.Timestamp expires_at_utc = 6;`
  - `ProfileUploadResponseProto`:
    - `bool success = 1;`
    - `string error_message = 2;`
  - `ProfileDeleteRequestProto`:
    - `bytes blinded_locator = 1;` (32-byte `BlindedLocator`)
    - `bytes write_token = 2;` (32-byte `WriteToken` capability)
  - `ProfileDeleteResponseProto`:
    - `bool success = 1;`
    - `string error_message = 2;`
  - `BlindBlobDownloadRequestProto`:
    - `bytes blob_id = 1;` (32-byte SHA-256 blob locator)
  - `BlindBlobChunkProto`:
    - `bytes chunk_data = 1;`
    - `uint32 chunk_index = 2;`
    - `bool is_final_chunk = 3;`
- **Service**:
  - `service UnauthenticatedDeliveryService`:
    - `rpc DeliverDirect(SealedEnvelopeProto) returns (DeliveryAckProto);` (Direct 1:1 P2P ingress over ephemeral TLS)
    - `rpc EnqueueMailbox(SealedEnvelopeProto) returns (EnqueueAckProto);` (Deposit into recipient's store-and-forward relay queue)
    - `rpc GetProfile(ProfileFetchRequestProto) returns (ProfileFetchResponseProto);` (Unauthenticated capability profile fetch; zero requester auth/metadata)
    - `rpc UploadProfile(ProfileUploadRequestProto) returns (ProfileUploadResponseProto);` (Unauthenticated capability profile upload authenticated solely by `WriteToken`)
    - `rpc DeleteProfile(ProfileDeleteRequestProto) returns (ProfileDeleteResponseProto);` (Unauthenticated profile slot deletion on key rotation)
    - `rpc DownloadBlindBlob(BlindBlobDownloadRequestProto) returns (stream BlindBlobChunkProto);` (Unauthenticated channel-independent blind blob retrieval)

#### 2. `authenticated_relay.proto` (Mailbox Ownership, Pre-Key Directory & Stream Management)
- **Trust Boundary**: Challenge-response / signature authenticated. Restricted exclusively to the private key holder of the `PublicIdentityId` / caller identity.
- **Security Rationale (Mirroring Signal)**:
  - `FetchPreKeyBundle` requires authenticated requester identity verification to prevent anonymous scrapers from exhausting one-time prekeys (OPKs) and crawling directory listings, while enabling per-account rate limiting.
- **Messages**:
  - `MailboxStreamClientMessage`:
    - `oneof payload`:
      - `AuthResponseProto auth_response = 1;` (Signature over server-issued challenge nonce)
      - `MailboxAckProto ack = 2;` (Confirms client receipt and triggers atomic deletion of queued message)
  - `MailboxStreamServerMessage`:
    - `oneof payload`:
      - `AuthChallengeProto auth_challenge = 1;` (Server-generated random 32-byte challenge nonce)
      - `AuthResultProto auth_result = 2;` (Authentication success or error code)
      - `QueuedSealedEnvelopeProto envelope = 3;` (Queued `SealedEnvelopeProto` accompanied by unique `bytes ack_id`)
  - `RegisterMailboxRequest`:
    - `bytes owner_identity_key = 1;`
    - `bytes routing_token = 2;`
    - `bytes authorized_delivery_token = 3;`
    - `bytes signature = 4;` (Signs `routing_token + authorized_delivery_token` to prove ownership)
  - `RegisterMailboxResponse`: `bool success = 1;`
  - `DrainMailboxRequest`: `bytes routing_token = 1;`, `bytes auth_token = 2;`
  - `DrainMailboxResponse`: `repeated QueuedSealedEnvelopeProto envelopes = 1;`
  - `PublishPreKeyBundleRequest`:
    - `bytes identity_key = 1;`
    - `bytes signed_prekey = 2;`
    - `bytes prekey_signature = 3;`
    - `bytes signed_prekey_id = 4;`
    - `repeated OneTimePreKeyProto one_time_prekeys = 5;`
    - `bytes signature = 6;`
  - `PublishPreKeyBundleResponse`: `bool success = 1;`
  - `FetchPreKeyBundleRequest`:
    - `bytes recipient_routing_token = 1;`
    - `bytes requester_identity_key = 2;`
    - `bytes requester_signature = 3;` (Signs `recipient_routing_token + timestamp_utc` proving requester identity)
    - `google.protobuf.Timestamp timestamp_utc = 4;`
  - `FetchPreKeyBundleResponse`:
    - `bytes identity_key = 1;`
    - `bytes signed_prekey = 2;`
    - `bytes prekey_signature = 3;`
    - `bytes signed_prekey_id = 4;`
    - `optional bytes one_time_prekey = 5;`
    - `optional bytes one_time_prekey_id = 6;`
  - `OneTimePreKeyProto`: `bytes key_id = 1;`, `bytes public_key = 2;`
- **Service**:
  - `service AuthenticatedRelayService`:
    - `rpc ConnectMailboxStream(stream MailboxStreamClientMessage) returns (stream MailboxStreamServerMessage);` (Duplex streaming connection for real-time pushed messages with ACK flow control)
    - `rpc RegisterMailbox(RegisterMailboxRequest) returns (RegisterMailboxResponse);` (Registers authorized delivery tokens)
    - `rpc DrainMailbox(DrainMailboxRequest) returns (DrainMailboxResponse);` (Unary fallback for intermittent polling)
    - `rpc PublishPreKeyBundle(PublishPreKeyBundleRequest) returns (PublishPreKeyBundleResponse);` (Authenticated prekey publishing)
    - `rpc FetchPreKeyBundle(FetchPreKeyBundleRequest) returns (FetchPreKeyBundleResponse);` (Authenticated prekey lookup with OPK rate-limiting & anti-scraping enforcement)

#### 3. `anonymous_group.proto` (Zero-Knowledge Group Relay Service)
- **Trust Boundary**: Zero-Knowledge membership verification. Group members prove authorization to read and write without revealing their identities to the relay.
- **Messages**:
  - `GroupStreamClientMessage`:
    - `bytes conversation_id = 1;`
    - `uint32 current_epoch = 2;`
    - `bytes zk_presentation_proof = 3;` (Proves membership in the current epoch)
  - `GroupStreamServerMessage`:
    - `oneof payload`:
      - `GroupBroadcastEnvelope broadcast = 1;` (Real-time group payload dispatched to subscribers)
      - `EpochMutationNotification mutation = 2;` (Alerts members that a roster change occurred)
  - `GroupBroadcastEnvelope`:
    - `bytes conversation_id = 1;`
    - `uint32 epoch = 2;`
    - `uint32 sender_key_iteration = 3;`
    - `bytes ciphertext = 4;`
    - `bytes author_signature = 5;`
  - `EpochMutationNotification`:
    - `bytes conversation_id = 1;`
    - `uint32 new_epoch = 2;`
  - `DispatchGroupMessageRequest`:
    - `bytes conversation_id = 1;`
    - `uint32 epoch = 2;`
    - `bytes zk_presentation_proof = 3;` (Verifies proof over `SHA256(ciphertext)`)
    - `uint32 sender_key_iteration = 4;`
    - `bytes ciphertext = 5;`
    - `bytes author_signature = 6;`
  - `DispatchGroupMessageResponse`: `bool accepted = 1;`
  - `CommitRosterMutationRequest`:
    - `bytes conversation_id = 1;`
    - `uint32 base_epoch = 2;`
    - `bytes new_roster_blob = 3;`
    - `repeated bytes new_routing_tokens = 4;`
    - `bytes zk_presentation_proof = 5;` (Verifies proof over mutation transcript hash)
  - `CommitRosterMutationResponse`: `bool committed = 1;`, `uint32 new_epoch = 2;`
  - `FetchGroupStateRequest`: `bytes conversation_id = 1;`, `bytes zk_presentation_proof = 2;`
  - `FetchGroupStateResponse`: `uint32 current_epoch = 1;`, `bytes encrypted_roster_blob = 2;`
- **Service**:
  - `service AnonymousGroupRelayService`:
    - `rpc SubscribeGroupStream(GroupStreamClientMessage) returns (stream GroupStreamServerMessage);` (Server-streaming subscription to live channel updates)
    - `rpc DispatchGroupMessage(DispatchGroupMessageRequest) returns (DispatchGroupMessageResponse);` (Anonymous broadcast dispatch)
    - `rpc CommitRosterMutation(CommitRosterMutationRequest) returns (CommitRosterMutationResponse);` (Roster epoch transition)
    - `rpc FetchGroupState(FetchGroupStateRequest) returns (FetchGroupStateResponse);` (State catch-up)

#### 4. `session.proto` (Inner Cryptographic Packaging)
Carried inside `SealedEnvelopeProto.encrypted_package` (opaque to relays and passive observers):
- **Messages**:
  - `RatchetHeaderProto`: `bytes ratchet_public_key = 1;`, `uint32 counter = 2;`, `uint32 previous_chain_length = 3;`
  - `DirectSessionPackageProto`:
    - `RatchetHeaderProto header = 1;`
    - `bytes nonce = 2;`
    - `bytes ciphertext = 3;`
  - `GroupSessionPackageProto`:
    - `bytes conversation_id = 1;`
    - `uint32 iteration = 2;`
    - `bytes ciphertext = 3;`
    - `bytes author_signature = 4;`
  - `HandshakeInvitationPackageProto`:
    - `bytes initiator_identity_key = 1;`
    - `bytes initiator_ephemeral_key = 2;`
    - `bytes signed_prekey_id = 3;`
    - `bytes onetime_prekey_id = 4;`
    - `bytes encrypted_payload = 5;`

#### 5. Application Plugins (`chat.proto`, `discovery.proto`, `file_transfer.proto`)
- `chat.proto`:
  - `ChatPayloadEnvelopeProto`:
    - `uint32 type = 1;` (Wire discriminator: Message=1, Edit=2, Tombstone=3, Reaction=4, ReadReceipt=5, SystemNotice=6, GroupInvitation=7, GroupInvitationResponse=8)
    - `bytes data = 2;` (Serialized inner message payload)
    - `bytes padding = 3;` (Pseudo-random stepped bucket padding expanding total frame to 256, 512, 1024, 2048, 4096 bytes to defeat traffic analysis)
  - `ChatMessageProto`:
    - `bytes id = 1;` (16-byte `PayloadId`)
    - `bytes channel_id = 2;` (16-byte `ChannelId`)
    - `bytes author_id = 3;` (32-byte `PublicIdentityId`)
    - `uint64 timestamp_unix_ms = 4;` (Clamped millisecond Unix timestamp defeating OS clock tick fingerprinting)
    - `string content = 5;` (Markdown rich text)
    - `repeated MentionProto mentions = 6;`
    - `optional ReplyContextProto reply_context = 7;`
    - `optional LinkPreviewProto link_preview = 8;`
    - `repeated MediaAttachmentProto attachments = 9;`
    - `optional uint32 expire_after_seconds = 10;`
    - `optional uint64 edited_at_unix_ms = 11;`
  - `ReplyContextProto`:
    - `bytes parent_payload_id = 1;`
    - `bytes parent_author_id = 2;`
    - *(Note: Explicitly zero quote snippet text on the wire to preserve cryptographic deniability and prevent leaking retracted messages).*
  - `MentionProto`:
    - `bytes identity_id = 1;`
    - `uint32 start_offset = 2;` (Measured strictly in UTF-8 byte spans)
    - `uint32 length = 3;` (Measured strictly in UTF-8 byte spans)
  - `MediaAttachmentProto`:
    - `bytes blob_id = 1;`
    - `optional string file_name = 2;` (Client-side sanitized: omitted/generic for photos, voice, video; preserved only for explicit document files)
    - `string content_type = 3;`
    - `uint64 unpadded_byte_size = 4;`
    - `uint32 width = 5;`
    - `uint32 height = 6;`
    - `bytes waveform = 7;`
  - `MessageEditProto`:
    - `bytes target_payload_id = 1;`, `bytes channel_id = 2;`, `bytes author_id = 3;`, `string new_content = 4;`, `repeated MentionProto new_mentions = 5;`, `uint64 edited_at_unix_ms = 6;`
  - `MessageTombstoneProto`:
    - `bytes target_payload_id = 1;`, `bytes channel_id = 2;`, `bytes requested_by = 3;`, `uint64 deleted_at_unix_ms = 4;`
  - `ReactionUpdateProto`:
    - `bytes target_payload_id = 1;`, `bytes channel_id = 2;`, `bytes author_id = 3;`, `string emoji = 4;`, `uint32 action = 5;`, `uint64 timestamp_unix_ms = 6;`
  - `ReadReceiptProto`:
    - `bytes channel_id = 1;`, `bytes reader_id = 2;`, `bytes up_to_payload_id = 3;`, `uint64 read_at_unix_ms = 4;`
  - `SystemNoticeProto`:
    - `bytes channel_id = 1;`, `uint32 type = 2;`, `bytes affected_identity = 3;`, `uint64 timestamp_unix_ms = 4;`
  - `GroupInvitationProto`:
    - `string invitation_id = 1;`, `bytes target_group_channel_id = 2;`, `string group_name = 3;`, `bytes inviter_id = 4;`, `optional string relay_endpoint = 5;`, `uint64 expires_at_unix_ms = 6;`
  - `GroupInvitationResponseProto`:
    - `string invitation_id = 1;`, `bytes target_group_channel_id = 2;`, `bytes responder_id = 3;`, `bool accepted = 4;`, `uint64 responded_at_unix_ms = 5;`
- `discovery.proto`:
  - `NearbyDiscoveryQueryProto`:
    - `bytes target_relay_id = 1;` (32-byte `PublicIdentityId` of target relay/peer)
    - `uint32 limit = 2;` (Clamped between 1 and 100 via `DiscoveryResultLimit`)
  - `NearbyDiscoveryResponseProto`:
    - `repeated PeerDiscoveryDescriptorProto peers = 1;`
  - `PeerDiscoveryDescriptorProto`:
    - `bytes public_identity_id = 1;` (32-byte `PublicIdentityId`)
    - `DeliveryRouteProto delivery_route = 2;`
  - `DeliveryRouteProto`:
    - `uint32 route_kind = 1;` (DirectP2P=1, RelayedOneToOne=2)
    - `bytes relay_identity_id = 2;`
    - `bytes delivery_token = 3;`
  - `DiscoveryRegistrationProto`:
    - `bytes peer_id = 1;`
    - `bytes relay_token = 2;`
    - `uint32 visibility = 3;` (Hidden=0, Discoverable=1)
  - `BlindedRendezvousQueryProto`:
    - `bytes blinded_locator = 1;` (32-byte HMAC-SHA256 locator)
    - `uint64 epoch = 2;` (24-hour UTC Unix day ratchet window)
  - `BlindedRendezvousResponseProto`:
    - `bool found = 1;`
    - `optional PeerDiscoveryDescriptorProto descriptor = 2;`
- `file_transfer.proto`:
  - `TransferManifestProto`:
    - `bytes manifest_id = 1;` (16-byte UUID in RFC 4122 big-endian order)
    - `string root_name = 2;`
    - `int64 total_size_bytes = 3;`
    - `int32 chunk_size_bytes = 4;`
    - `bytes merkle_root = 5;` (32-byte SHA-256 root)
    - `bytes concatenated_leaves = 6;` (Contiguous `N * 32`-byte SHA-256 leaf array with zero per-leaf tag overhead)
    - `repeated FileEntryProto files = 7;`
  - `FileEntryProto`:
    - `int32 index = 1;`, `string relative_path = 2;`, `int64 file_size_bytes = 3;`, `int64 start_offset = 4;`, `int64 last_modified_unix_seconds = 5;`
  - `FileTransferOfferProto`:
    - `bytes transfer_session_id = 1;`, `TransferBundleSummaryProto summary = 2;`, `ManifestDistributionProto manifest = 3;`, `bytes session_encryption_key = 4;`, `bytes base_nonce = 5;`, `bytes stream_auth_token = 6;`, `RelayRendezvousDescriptorProto relay_rendezvous = 7;`, `repeated DirectEndpointCandidateProto direct_candidates = 8;`
  - `FileTransferAcceptProto`:
    - `bytes transfer_session_id = 1;`, `repeated DirectEndpointCandidateProto receiver_candidates = 2;`, `uint32 routing_preference = 3;`, `int32 total_chunks = 4;`, `bytes resumption_bitfield = 5;`
  - `FileTransferCancelProto`:
    - `bytes transfer_session_id = 1;`, `string reason = 2;`

#### Serialization Adapters:
- **`ProtobufSessionWirePacker`** (`Percolator.Application.Ports.ISessionWirePacker`):
  - Packs domain cryptographic elements (`RatchetHeader`, `nonce`, `ciphertext`) into Protobuf `DirectSessionPackageProto` or `GroupSessionPackageProto`.
  - Wraps packed payloads into `SealedEnvelopeProto` with target `BlindedRoutingToken` and `DeliveryToken`.
  - Unpacks incoming `SealedEnvelopeProto` records into typed `InboundDirectEnvelope` or `InboundGroupEnvelope` instances for `Application`.
- **`ProtobufPayloadSerializer`** (`Percolator.PluginSdk.IPayloadSerializer`):
  - Serializes C# DTOs to binary using `Google.Protobuf.CodedOutputStream` and `IBufferWriter<byte>`.
  - Deserializes binary spans into strongly-typed C# DTOs via `Google.Protobuf.MessageParser<T>`.

---

### 4.3 Persistence, Database Repositories & Fast-Path Query Services

Database Engine: Encrypted SQLite using SQLCipher (`SQLitePCLRaw.bundle_e_sqlcipher` with EF Core or Dapper).

#### Architectural Guardrails for Persistence:
1. **Modular Schema Partitioning vs. Monolithic DbContext**:
   - Rather than repeating the legacy pitfall of a single monolithic 38-`DbSet` `PercolatorDbContext`, database access is cleanly partitioned:
     - `OperationalDbContext`: High-frequency transactional outbox jobs, inbound ingress queues, and live transport state.
     - `VaultDbContext`: Encrypted identity keys, signed pre-keys, and ratchet session states.
     - `ChatHistoryDbContext`: Channel messages, reaction blobs, and delivery receipts.
   - Prevents table lock contention on SQLite, accelerates application cold starts, and minimizes migration friction.
2. **Deterministic Cryptographic BLOB Mapping**:
   - EF Core LINQ equality over `byte[]` arrays (`x.RatchetPublicKey == key.ToArray()`) translates to pointer/reference comparisons or generates runtime evaluation warnings.
   - Repositories must configure explicit EF Core value converters with deterministic `ValueComparer<byte[]>` structural byte comparisons or utilize raw parameterized SQL / Dapper for indexed BLOB lookups.
3. **Defense-in-Depth Cryptographic Column Encryption**:
   - Replaces the legacy vulnerability where private keys (`ExportECPrivateKey()`) and chain keys (`ChainKey`, `SignatureKey`) were stored in raw plaintext BLOBs.
   - Key material stored on disk must always have column-level encryption using master keys derived from `ICredentialStorage`.

#### 1. Domain Aggregate & Plugin Repositories (Write / Consistency Enforcing)
- **`IOutboxRepository`** (`Percolator.Application.Delivery.Ports`):
  - Persists outbound messages (`OutboxJob`) in transactional storage.
  - Supports FIFO retrieval of pending jobs, status progression (`Pending` $\rightarrow$ `InFlight` $\rightarrow$ `Delivered` / `Failed`), and persona black-holing (`PauseJobsForIdentityAsync`).
- **`IPeerContactRepository`** (`Percolator.Domain.Identities.Ports`):
  - Stores `PeerContact` records, primary identity keys, authorized secondary devices, and trust levels.
- **`IRatchetSessionRepository`** (`Percolator.Application.Ports`):
  - Persists active `DirectRatchetSession` states (root key, current chain keys, skipped message key cache) under encryption.
- **`IGroupReceiverSessionRepository`** (`Percolator.Application.Ports`):
  - Persists active `GroupReceiverSession` states (channel, author ID, author device ID, current iteration, chain key, skipped message keys) under encryption.
- **`IGroupSenderKeyRepository`** (`Percolator.Application.Ports`):
  - Persists active `GroupSenderKeyRatchet` states (channel, author ID, author device ID, current iteration, chain key, Ed25519 signing key) under encryption.
- **`IPendingHandshakeRepository`** (`Percolator.Application.Ports`):
  - Persists pending inbound handshake envelopes (`InboundHandshakeEnvelope`) and encrypted greeting payloads while an inbound contact request is awaiting user approval (`PendingApproval`).
  - Supports retrieval by `(RecipientIdentityId, SenderIdentityId)` and atomic deletion upon handshake completion or contact rejection.
- **`IPrivatePreKeyStore`** (`Percolator.Domain.Identities.Ports`):
  - Persists local private signed pre-keys and pools of private one-time pre-keys, supporting atomic retrieval and consumption by key ID for inbound X3DH responder handshakes.
- **`IGroupCredentialsRepository`** (`Percolator.Domain.Security.Ports`):
  - Persists client-side `GroupCredentials` (master keys, auth credential MACs, blob keys) under encryption.
- **`IChannelRepository`** (`Percolator.Domain.Channels.Ports`):
  - Persists direct channels (`DirectChannel`) and group channels (`GroupChannel`) with member roles, epochs, and encrypted payload logs (`ChannelPayload`). Serves as the single authoritative persistence store for all channel state across applications.
- **`IRelayPreKeyDirectoryRepository`** (`Percolator.Domain.Relays.Ports`):
  - Backs the relay pre-key hosting directory with paging, expiration cleanup, and quota enforcement.
- **`IRelayProfileRepository`** (`Percolator.Domain.Delivery.Ports`):
  - **Adapter**: `SqliteRelayProfileRepository`. Backs `RelayHostedProfileSlot` aggregates on the `relay_profile_slots` table (`blinded_locator` BLOB PRIMARY KEY, `write_token` BLOB, `nonce` BLOB, `ciphertext` BLOB, `revision` INTEGER, `updated_at_utc` INTEGER, `expires_at_utc` INTEGER). Supports $O(1)$ indexed slot lookups (`GetSlotAsync`), atomic slot creation/updates (`SaveSlotAsync`), and write-token verified deletion (`DeleteSlotAsync`).
- **`IChatMessageRepository`** (`Percolator.Apps.Chat.Ports`):
  - **Adapter**: `SqlChatMessageRepository`. Persists and updates rich chat messages (`chat_messages`), mentions (`chat_message_mentions`), attachments (`chat_message_attachments`), reactions (`chat_reactions`), and read cursors (`chat_read_receipts`). Handles atomic message insertions, 24-hour edits, tombstones, and soft/hard expiration.
- **`IGroupInvitationRepository`** (`Percolator.Apps.Chat.Ports`):
  - **Adapter**: `SqlGroupInvitationRepository`. Persists `chat_group_invitations` tracking inbound and outbound invitations with consent status (`Pending`, `Accepted`, `Declined`, `Expired`).
- **`IUnknownGroupMessageCacheRepository`** (`Percolator.Apps.Chat.Ports`):
  - **Adapter**: `SqlUnknownGroupMessageCacheRepository`. Persists bounded out-of-order group messages (`chat_unknown_group_messages`) awaiting author sender key distribution.
- **`ITransferSessionRepository`** (`Percolator.Apps.FileTransfer.Ports`):
  - Persists active and historical file transfer sessions, manifests, bitfields, and swarm peer caches.
- **`IRelayPeerDirectory`** (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting`):
  - **Adapter**: `SqliteRelayPeerDirectory`. Backed by `disc_relay_peer_registrations` (`peer_id`, `delivery_token`, `visibility`, `last_registered_at_utc`). Stores registered discoverable peers on sovereign relays. Enforces admission gate validation (`IDiscoveryAdmissionGate` checking active Signed PreKey and handshake completion), respects relay policy (`Disabled`, `Open`, `BlindedContactsOnly`), peer visibility (`Hidden`, `Discoverable`), and bounds candidate results to `DiscoveryResultLimit`.

#### 2. Fast-Path Read Query Adapters (Bypassing Domain Aggregates via Direct SQL / Dapper Projections)
- **`SqlChatMessageQueryService`** (`Percolator.Apps.Chat.Ports.IChatMessageQueryService`):
  - Queries indexed `chat_messages` and joined reaction/attachment tables via direct paged SQL (`WHERE channel_id = @channelId AND (expires_at_utc IS NULL OR expires_at_utc > @now) AND sequence < @beforeSeq ORDER BY sequence DESC LIMIT @limit`). Returns lightweight `ChatMessageReadModel` instances without hydrating domain aggregates.
- **`SqlConversationListQueryService`** (`Percolator.Apps.Chat.Ports.IConversationListQueryService`):
  - Direct SQL query aggregating active channels, last message snippet, timestamp, and unread counts for the UI channel list.
- **`SqlGroupInvitationQueryService`** (`Percolator.Apps.Chat.Ports.IGroupInvitationQueryService`):
  - Fast query for pending group invitations awaiting user consent (`WHERE status = 'Pending' AND expires_at_utc > @now`).
- **`SqlOutboxQueryService`** (`Percolator.Application.Delivery.Ports.IOutboxQueryService`):
  - Reads pending/failed outbox job summaries for UI diagnostic dashboards without hydrating byte payload buffers.
- **`SqlPeerContactQueryService`** (`Percolator.Application.Ports.IPeerContactQueryService`):
  - Fetches contact book rows, pending contact requests (`ContactState.PendingApproval`), and reachability summaries without loading private keys or device link proofs. Projects `ContactState State` into `PeerContactSummaryReadModel` and `PeerContactDetailReadModel` for UI contact lists and incoming request approval badges.
- **`SqliteRelayPeerDirectory`** (`Percolator.Apps.Discovery.Features.RelayDirectoryHosting.IRelayPeerDirectory`):
  - Fast-path paged SQL query over `disc_relay_peer_registrations` projecting candidate discoverable peers directly into lightweight `PeerDiscoveryDescriptor` instances without hydrating domain aggregates.
- **`SqlFileTransferQueryService`** (`Percolator.Apps.FileTransfer.Ports.IFileTransferQueryService`):
  - Reads active file transfer task progress, byte rates, and manifest catalogs without locking active chunk transfer state machines.\

---

### 4.4 Ingress Edge Filtering, Stream Registry & Envelope Unwrapping (`Percolator.Application.Ports`)
- **Adapters**:
  - **`IIngressFilterService`** (`Percolator.Application.Ports`):
    - Enforces blacklist filtering against blocked identities (`PeerTrustLevel.Blocked`), sender rate limits, and identity dormancy status.
  - **`IStreamRegistry`** (`Percolator.Application.Ports`):
    - Tracks active, open gRPC duplex/server-streaming connections for direct peers and home relays.
    - Detects HTTP/2 connection drops and backpressure flow-control saturation (`StreamWriteResult`).
  - **`IPeerReachabilityService`** (`Percolator.Application.Routing`):
    - Evaluates direct reachability (via active connection or private rendezvous descriptor) and resolves home relay mailboxes for offline peers.
  - **`SealedEnvelopeUnwrapper`** (`Percolator.Application.Ports.ISealedEnvelopeUnwrapper`):
    - Bridges relay `MailboxEnvelope` instances to strongly typed `InboundEnvelope` objects using `ProtobufSessionWirePacker`.

---

### 4.5 Network Transport, Streaming & Dispatching
- **Adapters**:
  - **`ITransportDispatcher`** (`Percolator.Application.Delivery.Ports`):
    - Direct 1:1: Invokes `UnauthenticatedDeliveryService.DeliverDirect` against peer's ephemeral TLS endpoint.
    - Relayed 1:1: Invokes `UnauthenticatedDeliveryService.EnqueueMailbox` against recipient's home relay using `DeliveryToken``.
    - Relayed Group: Invokes `AnonymousGroupRelayService.DispatchGroupMessage` against shared channel relay with ZK membership proof.
  - **`UnauthenticatedDeliveryEndpoint` (ASP.NET Core gRPC Service)**:
    - Implements `UnauthenticatedDeliveryService` on **both Relay nodes and Direct Peer nodes** (running on their local ephemeral listening port).
    - Validates packet size and `DeliveryToken` validity, invokes ingress edge filters (`IIngressFilterService`), and hands unpacked envelopes to `InboundIngressPipeline`.
    - Handles unauthenticated profile capability lookups (`GetProfile`), capability uploads (`UploadProfile`), and slot deletions (`DeleteProfile`) delegating directly to `IRelayProfileRepository.GetSlotAsync / SaveSlotAsync / DeleteSlotAsync`. Enforces anti-scraping IP rate limits, write token verification, and maximum payload size bounds ($\le 1040$ B).
    - Streams unauthenticated blind avatar/media blobs (`DownloadBlindBlob`) via `IBlobStorageService` under `StorageMode.PublicRelayBlob` without requiring peer authentication or channel associations.
  - **Primary Container Service Activator (`PrimaryContainerServiceActivator<T>`)**:
    - *Pattern*: Implements `IGrpcServiceActivator<T>` to bridge Kestrel's secondary gRPC listener container to the host application's primary DI container (`IServiceProvider`).
    - *Lifecycle*: Dynamically creates an `IServiceScope` from the primary container per incoming gRPC request, resolves service dependencies, and guarantees deterministic scope cleanup in `ReleaseAsync()`.
  - **gRPC Interceptor Pipeline**:
    - **`IdentityReadinessInterceptor`**: Global interceptor that verifies `IActiveIdentityAccessor.IsActive`. If the local node or identity vault is locked (e.g., awaiting master passphrase unlock), rejects incoming RPCs with `StatusCode.Unavailable` ("Node not ready (no active identity)."), protecting uninitialized stores and crypto engines.
    - **`DeliveryCertificateAuthInterceptor`**: Intercepts authenticated relay and pre-key gRPC routes (Unary and Duplex streaming) to validate caller metadata (`x-percolator-sender-public-identity-id`, anti-replay `x-percolator-timestamp` window, and `x-percolator-signature` challenge proof) before handler execution.
  - **Thread-Safe gRPC Channel Factory with `Lazy<T>` (`PeerGrpcChannelFactory`)**:
    - Employs `ConcurrentDictionary<string, Lazy<GrpcChannel>>` keyed by `https://{host}:{port}`.
    - The `Lazy<GrpcChannel>` wrapper guarantees that only a single physical HTTP/2 connection pool is created even under concurrent dial races.
    - **Teardown Policy**: On application shutdown, concurrently calls `channel.ShutdownAsync()` with a 2-second timeout, followed by `.Dispose()` on all channels to terminate hung OS socket handles cleanly.
  - **Bounded Live Relay Stream Dispatcher**:
    - Replaces legacy unbounded channels with `Channel.CreateBounded<ServerRelayStream>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.Wait })` to prevent out-of-memory memory leaks when streaming to slow or degraded clients.
    - Uses concurrent tasks to separate incoming client ACKs (`requestStream.ReadAllAsync`) from outgoing live frame pumping (`responseStream.WriteAsync`).
    - Flushes offline/pending outbox egress jobs atomically upon client connection before streaming live frames.
    - Unregisters channels cleanly in a `finally` block on client disconnect.
  - **`RelayMailboxStreamWorker` (`IHostedService`)**:
    - Maintains resilient duplex stream to user's home relay via `AuthenticatedRelayService.ConnectMailboxStream`.
    - Handles challenge-response authentication with local `IdentityKey`.
    - Receives queued `SealedEnvelopeProto` messages, feeds them into `InboundIngressPipeline`, and returns `MailboxAckProto` confirmations upon successful persistence.
    - Automatically reconnects with exponential backoff and jitter on socket/HTTP-2 disconnects.
  - **`RelayGroupStreamWorker` (`IHostedService`)**:
    - Manages live server-streaming subscriptions (`AnonymousGroupRelayService.SubscribeGroupStream`) to host relays.
    - Presents ZK membership proofs for each active group channel.
    - Feeds broadcast group envelopes directly into `InboundIngressPipeline`.
  - **Ephemeral Direct P2P TLS 1.3 Transport (Anonymity & Blind-Trust Architecture)**:
    - **Security Architecture**: TLS does *not* authenticate peers; authentic end-to-end identity and secrecy is provided entirely by Signal Double Ratchet & X3DH application payloads. The transport layer's sole responsibility is wire encryption and network-level metadata obfuscation (masking HTTP/2 framing, gRPC route names, and packet lengths from passive network observers).
    - **Blind Trust Client Verification**: `SocketsHttpHandler` configures `RemoteCertificateValidationCallback = (_, _, _, _) => true`. This decouples TLS certificates from permanent identity keys, preventing network-level linkability/correlation of IP endpoints to identities.
    - **SSRF & LAN Endpoint Boundary Validation (`CallbackEndpointValidator`)**:
      - Remote peer endpoints and callback hosts provided via invitation links (`percolator://{host}[:{port}]/{publicKey}`) or discovery records are strictly validated before network connections are initiated.
      - Automatically rejects loopback (`127.0.0.1`), private RFC1918 subnets (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), link-local (`169.254.0.0/16`), and IPv6 private/ULA/link-local (`fc00::/7`, `fe80::/10`) to defend against Server-Side Request Forgery and malicious intranet port scanning, unless explicitly permitted by developer configuration flags (`AllowLoopback = true`, `AllowLan = true`).
    - **Dynamic Port Range Probing & Multi-Identity Contention**:
      - The protocol decouples clients and identities from strict single-port bindings. Each running node/identity operates across a sane, configurable port range (e.g. 5200–5299, roughly one port per active identity/relay).
      - **Optional Invitation Port**: When an invitation link omits the port (`percolator://{host}/{publicKey}`), the P2P transport dialer attempts connections across the configured port range to bring the direct channel online. Only when a user explicitly specifies a strict port in the invitation link does the dialer bypass port range probing.
      - **Port Contention Resolution**: On startup, nodes query active listening ports and dynamically claim an open port within the range (`SqliteReservedPortQuery`), enabling seamless multi-identity operation on the same machine without port collision.
    - **Portable Ephemeral Certificate Provider (`InMemoryTransportCertificateProvider`)**:
      - On application startup, generates a fresh, disposable ECDSA P-256 (`ECCurve.NamedCurves.nistP256`) keypair and in-memory self-signed certificate with `CN=Percolator-Ephemeral`, `KeyUsageFlags.DigitalSignature`, and loopback SANs (`localhost`, `127.0.0.1`, `::1`).
      - On Linux, macOS, and portable Kestrel OpenSSL servers, certificates remain purely in-memory with zero disk I/O.
      - *(Note: On Windows SChannel, ephemeral in-memory certificates fail TLS 1.3 handshakes. `Percolator.Infrastructure.Windows` provides `WindowsSChannelCertificateProvider` to handle PKCS#12 disk loading, Windows Defender write-retry backoff, and CNG key deletion).*
  - **Relay & Peer Client Adapters**:
    - Publishes `PreKeyBundle` uploads to relay identities via `AuthenticatedRelayService.PublishPreKeyBundle`.
    - Fetches remote peer pre-key bundles from relays out-of-band via `AuthenticatedRelayService.FetchPreKeyBundle`, authenticating with local `IdentityKey` signature to defeat OPK exhaustion attacks.
    - Queues store-and-forward mailbox envelopes via `UnauthenticatedDeliveryService.EnqueueMailbox` using recipient `DeliveryToken` / `BlindedRoutingToken`.
    - Fetches encrypted profile ciphertext packages anonymously from relays or direct peers out-of-band via `UnauthenticatedDeliveryService.GetProfile` presenting solely the recipient `BlindedLocator`.
    - Publishes encrypted profile ciphertext packages to relays out-of-band via `UnauthenticatedDeliveryService.UploadProfile` presenting solely `BlindedLocator` and `WriteToken`.
    - Deletes stale profile slots from relays upon key rotation via `UnauthenticatedDeliveryService.DeleteProfile` presenting `BlindedLocator` and `WriteToken`.
  - **Discovery Client & PreKey Adapters (`Percolator.Apps.Discovery.Ports`)**:
    - **`DiscoveryQueryClient`** (`INearbyDiscoveryQueryClientPort`):
      - Connects to a target relay or peer gRPC endpoint to execute `NearbyDiscoveryQueryProto` requests. Enforces `DiscoveryResultLimit` clamping (1–100), deserializes `NearbyDiscoveryResponseProto`, and returns sanitized `IReadOnlyList<PeerDiscoveryDescriptor>` with `DeliveryRoute.RelayedOneToOne`.
    - **`DiscoveryHandshakeAdapter`** (`IDiscoveryHandshakePort`):
      - Initiates mutual X3DH / Double Ratchet handshake with a discovered candidate peer. Bridges to the application handshake workflow / pending handshake queue to establish an authenticated direct or relayed E2EE session.
    - **`PreKeyAvailabilityAdapter`** (`IPreKeyAvailabilityPort`):
      - Checks local relay directory (`IRelayPreKeyDirectoryRepository`) or prekey registry to verify whether an active Signed PreKey (SPK) is available for admission gate validation.
    - **`PreKeyBundleProviderAdapter`** (`IPreKeyBundleProvider`):
      - Fetches the active prekey bundle (Identity Key, Signed PreKey + signature, and optional One-Time PreKey) for a discovered peer upon user connection intent, deferring bundle retrieval until Step 2 to prevent OPK exhaustion.

---

### 4.6 Platform & Discovery Adapters
- **`IDateTimeProvider`** (`Percolator.Domain.Common`):
  - `SystemDateTimeProvider` delegating to `DateTimeOffset.UtcNow`.
- **Portable `ICredentialStorage`** (`Percolator.Application.Ports.ICredentialStorage`):
  - **`PortablePassphraseCredentialStorage`**: Cross-platform master secret provider utilizing Argon2id key derivation from user master passphrase with AES-256-GCM encrypted local keyfile storage.\
  - *(Note: On Windows, `Percolator.Infrastructure.Windows` provides `WindowsDpapiCredentialStorage` to bind encryption directly to the Windows User DPAPI master key and apply Windows NTFS user-exclusive ACLs).*
- **Private Rendezvous & Locator Adapters** (`Percolator.Apps.Discovery`):
  - **`PrivateRendezvousService`**: Computes deterministic blinded locators ($\text{BlindedLocator} = \text{HMAC-SHA256}(K_{\text{rendezvous}}, \text{epoch})$) keyed by 24-hour UTC ratchet windows (`DiscoveryEpoch`) for sovereign relay rendezvous lookup, eliminating global DHT routing tables, XOR distance metrics, and broadcast sniffing.

---

### 4.7 Transient State Cleanup & Pruning Services (Research & Design)
- **Problem Statement**: Multiple transient and ephemeral tables accumulate stale records that must be pruned periodically without locking active database transactions or degrading ingress throughput.
- **Research Scope & Target Adapters**:
  - **`DisappearingMessageEvictionWorker`**: Scheduled cron worker (running every 30 seconds) calling `IChatMessageRepository.PurgeExpiredMessagesAsync(now)` to delete messages past `expires_at_utc` and invoking `IBlobStorageService.DeleteLocalCacheAsync` for deleted media blobs.
  - **`OutboxRetentionPruner`**: Evaluates retention window policies for `OutboxJob` rows (e.g., pruning `Delivered` jobs older than 7 days, purging dead-letter jobs that have exceeded `MaxRetryCount` and manual inspection windows).
  - **`RelayPreKeyDirectoryPruner`**: Evicts expired signed pre-key bundles and consumed or timed-out one-time pre-keys from `IRelayPreKeyDirectoryRepository` environmental records.\
  - **`RelayProfileSlotPruner`**: Scheduled background worker running periodically on relays. Evicts expired profile slots from `relay_profile_slots` in `SqliteRelayProfileRepository` where `expires_at_utc < now` or slots abandoned past retention windows (e.g. 30 days without update), reclaiming storage without locking active lookups.
  - **`UnknownGroupMessageCachePruner`**: Purges buffered group chat frames from `IUnknownGroupMessageCacheRepository` that exceed maximum TTL (e.g. 48 hours) where the author's sender key distribution was never received.
  - **`AbandonedInvitePruner`**: Scans inbound contact requests (`PeerContactState.PendingApproval`), pending handshake envelopes in `IPendingHandshakeRepository`, and group invitations (`PendingGroupInvitation`) exceeding local expiration policies (e.g. 14 days without user response) and marks or purges them.
  - **`RelayDiscoveryRegistrationPruner`**: Scans `disc_relay_peer_registrations` table in `Apps.Discovery` to evict expired registrations (e.g., registrations older than 7 days without renewal) or registrations whose corresponding Signed PreKey has expired.
  - **`TransferOfferReaperWorker`**: Scheduled cron worker (running every 15 minutes) calling `ITransferOfferReaperService.SweepAndExpireStaleOffersAsync(TimeSpan.FromHours(24))` to expire and persist stale staged file transfer offers in `ft_transfer_sessions` that have timed out without recipient consent (Lens 3).
- **Background Worker Resiliency Best Practices**:
  - **Async Scopes**: Workers must use `IServiceScopeFactory.CreateAsyncScope()` for deterministic asynchronous resource cleanup.
  - **Time Virtualization**: Depend strictly on .NET `TimeProvider` (`timeProvider.GetUtcNow()`, `Task.Delay(..., timeProvider, ct)`) to enable deterministic time manipulation and fast-forwarding in integration test suites.
  - **Proactive Soft Expiry**: Workers refreshing certificates, pre-keys, or tokens must inspect soft thresholds (e.g. `ExpiresAtUtc < now + TimeSpan.FromHours(4)`) rather than waiting for hard expiration, preventing downtime during transient network outages.
  - **Granular Error Discrimination**:
    - `OperationCanceledException`: Clean shutdown loop termination.
    - `CryptographicException`: Fatal configuration/corruption error; worker logs critical alert and terminates without busy-looping.
    - Transient network exceptions (`RpcException(Unavailable)`, `HttpRequestException`, `TimeoutException`): Triggers exponential backoff with jitter.
  - **Per-Item Fault Isolation**: Batch processing loops must wrap each entity/peer evaluation in an isolated `try/catch` block so a failure with a single peer cannot abort the entire background sweep.
  - **Bounded Batches**: Background pruners execute as scheduled `IHostedService` cron workers utilizing batch deletion with SQLite `LIMIT` clauses to avoid long-lived database write locks.

---

### 4.8 Encrypted Blob Storage, Chunk Streaming & Relay Pruning Adapters (`Percolator.Application.Ports`)
- **`StreamingAesGcmCryptoService`** (`Percolator.Application.Ports.IBlobCryptoService`):
  - **EXIF & Geolocation Metadata Scrubbing**: Strips invasive EXIF/XMP tags (GPS coordinates, camera serial numbers, device model) from raw image/video streams client-side before padding and encryption, guaranteeing complete location privacy.
  - **Stepped Bucket Padding**: Applies PKCS#7 or ISO/IEC 7816-4 padding to discrete boundaries (< 1 MB to 32 KB; 1–10 MB to 256 KB; > 10 MB to 1 MB) to prevent traffic analysis and file fingerprinting.
  - **Chunked STREAM AEAD**: Encrypts and decrypts in fixed $64\text{ KB}$ chunks using .NET `AesGcm` with sequential counter nonces ($\text{Nonce}_i = N_{\text{base}} \oplus i$) and 16-byte authentication tags.
  - **Bounded Memory & Zero LOH Allocations**: Rents chunk buffers from `ArrayPool<byte>.Shared`, ensuring memory consumption never exceeds $\le 64\text{ KB}$ regardless of multi-megabyte file size.
- **`StorageMode.PublicRelayBlob` Support**:
  - In addition to authenticated channel-bound blob storage, supports channel-independent blind blob hosting (such as encrypted user profile avatars).
  - Blobs uploaded under this mode are keyed purely by their SHA-256 ciphertext hash (`BlobId`) and served out-of-band to anyone with the locator via `UnauthenticatedDeliveryService.DownloadBlindBlob`, eliminating `ChannelId` leakage on the wire and decoupling avatar retrieval from chat channels.
- **`RelayBlobClient`** (`Percolator.Application.Ports.IRelayBlobClient`):
  - High-performance HTTP/2 or gRPC streaming client communicating with the channel's designated relay.
  - **Streaming Upload (`POST /blobs`)**: Streams chunked ciphertext out-of-band with real-time `TransferProgress` telemetry (bytes transferred, rate, ETA) and cancellation support. Attaches authentication headers (signed ticket for 1:1 relay; Group Epoch Token for group channels).
  - **Resumable Download (`GET /blobs/{sha256}`)**: Supports HTTP `Range: bytes={start}-{end}` to resume dropped downloads at chunk boundaries without restarting from byte 0.
- **`PeerStreamBlobClient`** (`Percolator.Application.Ports.IPeerStreamBlobClient`):
  - Multiplexed data stream client operating over active direct peer connections registered in `IStreamRegistry`.
  - Serves chunks on demand for 1:1 Direct P2P channels where both peers are online.
- **`LocalFileChunkStore`** (`Percolator.Application.Ports.ILocalChunkStore`):
  - Persistent file-system backed chunk store located in the application data directory (`%AppData%/Percolator/Blobs/`).
  - **Atomic File Writes**: Writes incoming chunks to a `.tmp` file and atomically commits via `File.Move(..., overwrite: true)` upon completion to prevent corrupted partial files.
  - **Content-Addressed Storage**: Files stored as `{sha256Hex}.blob`.
- **`RelayBlobRetentionPruner`** (`IHostedService`):
  - Background cron worker on relays executing periodic sweeps to delete expired blobs based on TTL (default 14 days).
  - Enforces disk storage high/low watermarks (evicting oldest expired blobs if disk usage exceeds 90%).

---

### 4.9 High-Performance File Transfer Adapters, Multi-Stream Engine & Relay Pipes (`Percolator.Apps.FileTransfer.Ports`)

Concrete realization of out-of-band high-speed croc-inspired and BitTorrent swarm mechanics, implementing ports defined in `Percolator.Apps.FileTransfer.Ports` using application-level transport DTOs:

#### 1. Multi-Stream Sockets & Binary Framing Engine
- **`TcpMultiStreamPool` & `TransferStream`** (`ITransferStreamPool`, `ITransferStreamPoolFactory`, `ITransferStream`):
  - Opens and manages a pool of 4 to 8 parallel multiplexed TCP/QUIC connections with `TCP_NODELAY = true`.
  - Implements the binary length-prefixed framing wire protocol using `StreamAuthToken`, `BaseNonce`, `ChunkNonce`, and `TransferKey`:
    - `0x01 STREAM_HANDSHAKE`: Handshake with `SessionId`, `StreamIndex`, `StreamAuthToken`, and `SenderPeerId`.
    - `0x02 BITFIELD`: Bitfield declaration of possessed chunks.
    - `0x03 HAVE`: Chunk verification announcement.
    - `0x04 REQUEST_CHUNK` & `0x05 CHUNK_DATA`: 1 MB chunk streaming with AES-256-GCM authenticated encryption/decryption using `AesGcm` and deterministic 12-byte `ChunkNonce` derived as $\text{BaseNonce} \oplus \text{BigEndianUInt96}(i)$.
    - `0x06 CANCEL_CHUNK`: Endgame chunk cancellation.
    - `0x07 CHOKE` / `0x08 UNCHOKE`: Transport backpressure signaling and request queue bounding (`MaxPendingRequestsPerPeer = 32`).
    - `0x09 INTERESTED` / `0x0A NOT_INTERESTED`: Swarm interest signaling for selective piece availability.
    - `0x0B KEEP_ALIVE`: Liveness ping frames.
    - `0x0C PEX_PEERS`: Out-of-band peer exchange gossip frames.
  - Work-stealing scheduler: Detects stalled streams and redistributes in-flight chunk requests to faster streams in the pool.

#### 2. Channel Relay Transfer Pipe (Client & Relay Service Endpoint)
- **`RelayTransferPipeClient`**:
  - Connects 4 to 8 parallel streams to the channel's designated relay endpoint: `POST /transfer-pipes/{sessionId}` (or WebSocket `GET /transfer-pipes/{sessionId}/ws`) using `RelayPipeEndpointUri` and `RelayPipeToken`.
  - Presents `PipeAuthToken = HMAC-SHA256(K_transfer, "relay-pipe-rendezvous")` in HTTP headers (`X-Pipe-Auth`).
  - Operates as a transparent binary stream pipe to the paired peer.
  - **Remote Manifest Blob Streaming Guard (Lens 3)**: When downloading out-of-band manifests via `ManifestBlobDescriptor`, wraps response stream in a counting bounded stream that strictly aborts transfer and closes the connection if payload size exceeds 8 MB (`MaxRemoteManifestBlobBytes = 8_388_608`), defending against unbounded memory inflation.
- **Relay Server Pipe Service (Relay-Side Execution)**:
  - Pairs incoming sender and receiver streams by `(SessionId, StreamIndex)`.
  - Executes zero-copy bidirectional socket splicing (`System.IO.Pipelines` or `PipeReader.CopyToAsync`).
  - **Zero Disk Usage**: Pure memory buffer pipe (bounded to 2 MB buffer per stream).
  - **Operator Opt-In Safeguards**: Configured via `RelayFileTransferCapabilities`:
    - `SupportsTransferPipes`: If false, relay rejects connection with HTTP 403 Forbidden.
    - `MaxPipeBandwidthBytesPerSec`: Bandwidth throttling per active pipe session.
    - `MaxConcurrentPipes`: Rejects new pipe negotiations if concurrent limit is reached.
    - Idle timeout: Closes pipe after 60 seconds of silence.

#### 3. Relay Blind Swarm Tracker (Client & Relay Service Endpoint)
- **`RelaySwarmTrackerClient`** (`IDistributedTrackerPort`):
  - Sends out-of-band HTTP `POST /swarms/{blindSwarmId}/announce` to channel relay.
  - Blind identifier: $\text{BlindSwarmId} = \text{HMAC-SHA256}(K_{\text{transfer}}, \text{"swarm-rendezvous"})$ (`SwarmRendezvousId`).
  - Receives list of active `DirectEndpointCandidate` records for the swarm without touching in-band ratchet channels.
  - **Proactive Unregistration (`IDistributedTrackerPort.UnregisterAsync`)**: Dispatches HTTP `DELETE /swarms/{blindSwarmId}/candidates?peerId={memberId}` with `X-Swarm-Auth` header upon session completion or cancellation, immediately purging endpoint records.
- **Relay Server Swarm Tracker Endpoint (Relay-Side Execution)**:
  - In-memory thread-safe dictionary: `ConcurrentDictionary<byte[], SwarmPeerRegistry>`.
  - Ephemeral 90-second sliding expiration. Entries purged if heartbeat stops.
  - Candidate deletion handler: `DELETE /swarms/{blindSwarmId}/candidates` removes matching peer entries immediately upon client departure.
  - Zero disk storage; zero awareness of channel identity or file contents.
  - Opt-in enforcement: If `SupportsSwarmTracker = false`, returns HTTP 501 Not Implemented, causing clients to fall back strictly to out-of-band PEX.

#### 4. Portable Desktop File Storage & Atomic File Finalization
- **`PortableDesktopFileStore`** (`IDesktopFileStorage`):
  - **Portable Pre-Allocation & Zero-Byte File Materialization**:
    - **Non-Empty Files (`FileSize.Bytes > 0`)**: Opens target files with `FileShare.ReadWrite | FileShare.Delete` and advances end-of-file pointer via `FileStream.SetLength(totalBytes)` (or POSIX `posix_fallocate`).
    - **Zero-Byte Files (`FileSize.Bytes == 0`)**: Directly creates the empty file at target path `{relativePath}` (without the `.percolator-part` staging suffix), applies `File.SetLastWriteTimeUtc` from manifest timestamp, and flushes immediately. This guarantees that 0-byte files are fully materialized from the start, avoiding dangling `.percolator-part` files on disk that never receive chunk verification callbacks.
  - **Path Resolution & Staging with `.percolator-part`**:
    - Combines `RootDirectoryPath` and `SanitizedRelativePath` to resolve `ResolvedFilePath`.
    - Writes incoming chunks to `{relativePath}.percolator-part` using `RandomAccess.WriteAsync(SafeFileHandle, ReadOnlyMemory<byte>, fileOffset, ct)`.
    - Protects incomplete files against antivirus scans, search indexers, and accidental execution.
  - **Atomic Finalization**:
    - Upon complete chunk verification of all blocks belonging to a file:
    - Flushes file buffers to disk.
    - Atomically renames `{relativePath}.percolator-part` to `{relativePath}` via `File.Move(..., overwrite: true)`.
    - Sets `File.SetLastWriteTimeUtc` from manifest timestamp (`FileLastModifiedUtc`).
  - **Resumption Bitfield Scanner**:
    - Reads existing `.percolator-part` and completed files on disk in chunk-sized blocks.
    - Computes SHA-256 over each block and compares against `MerkleTreeIndex` leaf hashes to construct the resumption `Bitfield`.
    - Zero-byte files are skipped during chunk offset mapping and verified strictly for filesystem existence, ensuring they do not offset or corrupt the piece bitmask.
  - *(Note: On Windows NTFS, `Percolator.Infrastructure.Windows` provides `WindowsSparseDesktopFileStore` which invokes Win32 `DeviceIoControl` with `FSCTL_SET_SPARSE` to avoid synchronous zero-fill overhead).*
  - **Security Risk & OS Path Sanitization Guardrail (Lens 3 Security)**:
    - *Path Traversal & Device Name Hijacking*: While the domain model enforces relative path invariants (`SanitizedRelativePath`), `PortableDesktopFileStore` bears the concrete infrastructure responsibility of validating the fully resolved destination path against operating system vulnerabilities before opening file handles:
      - **Win32 Reserved Device Names**: Explicitly rejects legacy DOS device namespaces (e.g., `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, `CONIN$`, `CONOUT$`) and any filename with these base names regardless of file extension (e.g. `nul.txt` or `aux.dat`).
      - **NTFS Alternate Data Streams (ADS)**: Prohibits colons (`:`) in relative paths to prevent hiding payloads or overriding stream attributes (`file.txt:evil.exe`).
      - **Canonical Boundary Verification**: Resolves the full canonical absolute path via `Path.GetFullPath` and verifies:
        `resolvedPath.StartsWith(canonicalRootPath, StringComparison.OrdinalIgnoreCase)`
        preventing symlink, directory junction, or hardlink escapes out of the destination root directory.
      - **Unicode Normalization & Hidden Segment Traversal (V-79)**: 
        Performs Unicode Normalization Form C (`NormalizationForm.FormC`) on all incoming relative paths before resolving to physical disk paths.
        Explicitly rejects homoglyphs (e.g. fullwidth solidus `／`, division slash `∕`, two-dot leader `‥`), ASCII control characters (`\u0000`–`\u001f`), trailing spaces (`foo `), and trailing dots (`foo.`) in directory and file segment names that cause Windows Win32 filesystems to truncate or silently collapse segment paths.

#### 5. Bandwidth Rate Limiter
- **`TokenBucketRateLimiter`** (`ITransferRateLimiter`):
  - Implements high-precision token-bucket algorithm using `PeriodicTimer` or fractional nanosecond timestamp tracking.
  - Enforces global speed caps (`MaxDownloadBytesPerSec`, `MaxUploadBytesPerSec`) and per-session overrides using `BandwidthLimit` and `TransferBandwidth`.
  - Dynamically throttles `Stream.ReadAsync` and `Stream.WriteAsync` loops in `TcpMultiStreamPool`.

#### 6. SQLite Repositories for File Transfer
- **`SqliteTransferSessionRepository`** (`ITransferSessionRepository`):
  - `ft_transfer_sessions`: Stores session state, role, initiator identity, participant topology JSON, root title, total bytes, chunk size, total chunks, target seed ratio, uploaded bytes, Merkle root BLOB, local destination path (`RootDirectoryPath`), AES session key (`TransferKey` encrypted under local master key), base nonce (`BaseNonce`), stream auth token (`StreamAuthToken`), relay URI, created/completed timestamps.
  - `ft_manifest_files`: Stores file index, sanitized relative path, file size in bytes, start offset, end offset, and sanitized last modified timestamp. Keyed by `(session_id, file_index)`.
  - `ft_session_bitfields`: Stores binary chunk bitfield BLOB representing local piece possession state for active downloads. Keyed by `session_id`.
  - `ft_swarm_members`: Stores connected member piece possession and flow control state (`session_id`, `peer_identity_id`, `bitfield_blob`, `choking_us`, `we_choking`, `interested_in_us`, `we_interested`, `joined_at_utc`). Keyed by `(session_id, peer_identity_id)`.
  - `ft_swarm_endpoints`: Stores discovered direct connection candidates (`session_id`, `peer_identity_id`, `host`, `port`, `candidate_kind`, `routing_preference`, `last_seen_utc`) for fast-resume without polluting the domain model with socket addresses.
- **`SqliteFilterPolicyRepository`** (`IFileTransferFilterPolicyRepository`):
  - `ft_filter_policies`: Stores owner identity ID and policy updated timestamp.
  - `ft_blocked_peers`: Stores blocked peer identities (`owner_identity_id`, `blocked_peer_identity_id`, `created_at_utc`).
  - `ft_blocked_relays`: Stores blocked relay URIs (`owner_identity_id`, `relay_uri`, `created_at_utc`).

#### 7. CQRS Transfer Query Service
- **`SqlFileTransferQueryService`** (`IFileTransferQueryService`):
  - Fast-path read-only service using Dapper/SQL direct queries against `ft_transfer_sessions` and `ft_manifest_files`.
  - Materializes `TransferSessionReadModel` and `ManifestFileReadModel` with real-time progress calculations, speed caps, seeding ratio, and per-file completion status without domain aggregate hydration overhead.

#### 8. Transfer Offer Reaper Worker
- **`TransferOfferReaperWorker`** (`ITransferOfferReaperService`, `IHostedService`):
  - Scheduled background worker executing every 15 minutes.
  - Invokes `SweepAndExpireStaleOffersAsync(TimeSpan.FromHours(24))` against `ITransferSessionRepository`.
  - Transitions abandoned/unaccepted staged sessions (`PendingConsent`) older than 24 hours to `Cancelled` via `session.ExpireOffer(clock)` and purges stale endpoints.

#### 9. Swarm Endpoint Retention Pruner
- **`SwarmEndpointRetentionPruner`** (`IHostedService`):
  - Scheduled background maintenance worker running daily.
  - Purges rows from `ft_swarm_endpoints` where `last_seen_utc` is older than 7 days, preventing unbounded database growth from transient swarm peers.
