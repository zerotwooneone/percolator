# Encrypted Profile Distribution Specification

## 1. Overview & Architectural Principles

This document defines the technical architecture for encrypted user profiles in Percolator. It adapts the capability-based privacy model pioneered by the **Signal Protocol** to Percolator's hybrid network topology, supporting both **serverless Direct 1:1 P2P connections** and **asynchronous store-and-forward Relay hosting**.

### 1.1 Academic & Industry Foundations (Signal Heritage & Citations)
This specification directly adopts and synthesizes the cryptographic invariants established by Open Whisper Systems / Signal:

1. **Signal Encrypted Profiles (August 2017)**:
   * *Reference*: [Signal Blog: Encrypted Profiles are now in Signal](https://signal.org/blog/encrypted-profiles/)
   * *Core Mechanism*: The server acts as an untrusted blind storage medium. The user generates a 256-bit symmetric `ProfileKey` client-side, encrypts their profile data locally with AES-GCM, and uploads only the ciphertext. The server never receives the key. The key is distributed as out-of-band capability metadata to trusted contacts via end-to-end encrypted message channels.
2. **Signal Sealed Sender & Delivery Tokens (October 2018)**:
   * *Reference*: [Signal Blog: Technology preview: Sealed Sender for Signal](https://signal.org/blog/sealed-sender/)
   * *Core Mechanism*: To eliminate metadata leakage on the server, senders do not present their identity to the routing server. Instead, a recipient's `ProfileKey` is used to deterministically derive an **Unidentified Delivery Token**. Presenting this token proves to the server that the sender is authorized by the recipient to deliver messages into their mailbox without disclosing the sender's identity.
3. **Signal Private Group System v2 & Anonymous Credentials (2019–2020)**:
   * *Reference*: [Signal Blog: Signal Private Group System](https://signal.org/blog/signal-private-group-system/) and [libsignal `zkcredential`/`zkgroup`](https://github.com/signalapp/libsignal)
   * *Core Mechanism*: Separation of lightweight metadata (names, bios, revision numbers, and capability keys $\le 1\text{ KB}$) from heavyweight encrypted media (avatars), coupled with blinded locators and unauthenticated/blinded token retrieval so the storage server cannot correlate profile requests with user identities or social graphs.

### 1.2 Core Tenets in Percolator
1. **Zero-Knowledge Hosting & Social Graph Invisibility**: Relays and intermediate network transports never possess profile keys, plaintext metadata, avatars, or social graph connections. Profile queries are executed via **unauthenticated capability-based gRPC ingress**, ensuring the Relay never learns which user connection is requesting which contact's profile.
2. **Capability-Based Access Control**: Possession of a user's 32-byte symmetric `ProfileKey` is the sole authorization token required to decrypt that user's profile metadata and avatar.
3. **Sealed Delivery Token Derivation**: A user's `DeliveryToken` (used to enqueue messages onto `RelayMailboxQueue`) is derived directly from their `ProfileKey`. Holding a contact's profile key is what authorizes dropping messages into their mailbox, eliminating relay spam without server-side sender tracking.
4. **Decoupled Metadata vs. Heavy Avatar Media**: Profile text metadata (`DisplayName`, `StatusBio`, `Revision`) is compact ($\le 1\text{ KB}$) and encrypted directly under `ProfileKey`. Avatars are stored as channel-independent blind encrypted blobs via [`BlobStorageService`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Services/BlobStorageService.cs) and referenced by a compact [`BlobReference`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.PluginSdk/BlobStorageMode.cs), eliminating channel ID leakage to the Relay and ensuring profile packages never exceed Percolator's 64 KB ingress limit.
5. **Decoupled Key Distribution via Chat Sub-Messages**: The symmetric `ProfileKey`, storage locator, and mailbox routing token are distributed end-to-end inside established Double Ratchet sessions as a specialized Chat sub-message (`AppId.Chat = 0x01`). The corresponding encrypted ciphertext is fetched asynchronously from the hosting endpoint (unauthenticated Relay ingress or direct peer stream).
6. **Strict Onion Architecture & Hexagonal Ports**: Cryptographic value objects (`ProfileKey`, `ProfileCiphertextPackage`, `BlindedProfileLocator`, `ProfileWriteToken`) reside strictly in `Percolator.Domain`. Relay storage is governed by a domain aggregate root (`RelayHostedProfileSlot`) enforcing write authorization, monotonic revisions, and size quotas.

---

## 2. Cryptographic Architecture & Key Derivation Hierarchy

```
                                  Identity Owner
                         (IK_Owner, Persistent ProfileKey)
                                        │
           ┌────────────────────────────┼───────────────────────────┬──────────────────────────┐
           ▼ (HKDF-1)                   ▼ (HKDF-2)                  ▼ (HKDF-3)                 ▼ (HKDF-4)
  BlindedProfileLocator      ProfileWriteToken           DeliveryToken               AvatarKey
       (32 bytes)                   (32 bytes)                  (32 bytes)                  (32 bytes)
           │                            │                           │                           │
           │ Opaque Lookup Key          │ Write / Overwrite Auth    │ Mailbox Enqueue Auth      │ STREAM AEAD Key
           ▼                            ▼                           ▼                           ▼
  Relay Profile Store          Relay Profile Store         Relay Mailbox Queue         Blind Blob Storage
  (Unauth Ingress)             (Verified by Aggregate)     (Sealed Sender)             (Channel-Independent)
```

### 2.1 The Profile Key (`ProfileKey`)
* **Specification**: A 32-byte cryptographically secure random value (`AES-256`).
* **Generation**: Generated client-side on identity initialization, stored in local encrypted identity storage, and rotated upon contact revocation or channel security events.
* **Domain Location**: Resides in `Percolator.Domain.Security.ValueObjects`.
* **Memory Hygiene**: Encapsulated in `ProfileKey` implementing `IDisposable` with `CryptographicOperations.ZeroMemory` upon disposal.

### 2.2 Key Derivation Hierarchy (HKDF-SHA256)
All operational tokens are derived deterministically using HKDF-SHA256 (`ICryptoEngine.HkdfSha256`):

1. **`BlindedProfileLocator` (32 bytes)**:
   The opaque primary key under which the profile metadata package is indexed on the Relay:
   $$\text{BlindedProfileLocator} = \text{HKDF-SHA256}(\text{IKM} = \text{IK}_{\text{Owner}}.\text{Span}, \text{Salt} = \text{ProfileKey}.\text{Span}, \text{Info} = \text{"PercolatorProfileLocatorV2"}, \text{Len} = 32)$$
   *Privacy Property*: 256-bit collision resistance. Neither the relay nor network observers can correlate this locator with the user's `PublicIdentityId` without possessing both $\text{IK}_{\text{Owner}}$ and $\text{ProfileKey}$.

2. **`ProfileWriteToken` (32 bytes)**:
   The write-authorization credential required to create, overwrite, or delete the profile slot in the Relay's `RelayHostedProfileSlot`:
   $$\text{ProfileWriteToken} = \text{HKDF-SHA256}(\text{IKM} = \text{IK}_{\text{Owner}}.\text{Span}, \text{Salt} = \text{ProfileKey}.\text{Span}, \text{Info} = \text{"PercolatorProfileWriteTokenV1"}, \text{Len} = 32)$$
   *Security Property*: Enforces write authentication without exposing identity. The relay aggregate only permits writes to `BlindedProfileLocator` if the caller proves possession of `ProfileWriteToken`.

3. **`DeliveryToken` (32 bytes)** (Signal Sealed Sender Delivery Token Parity):
   The token presented when enqueuing messages into the recipient's mailbox:
   $$\text{DeliveryToken} = \text{HKDF-SHA256}(\text{IKM} = \text{IK}_{\text{Owner}}.\text{Span}, \text{Salt} = \text{ProfileKey}.\text{Span}, \text{Info} = \text{"PercolatorDeliveryTokenV1"}, \text{Len} = 32)$$
   *Security Property*: Automatically synchronizes contact approval with mailbox admission. Only peers possessing Alice's `ProfileKey` can calculate her `DeliveryToken` to drop messages into her `RelayMailboxQueue`.

4. **`AvatarKey` (32 bytes)**:
   The symmetric key used to encrypt the out-of-band avatar image:
   $$\text{AvatarKey} = \text{HKDF-SHA256}(\text{IKM} = \text{ProfileKey}.\text{Span}, \text{Salt} = \emptyset, \text{Info} = \text{"PercolatorAvatarKeyV1"}, \text{Len} = 32)$$

---

## 3. Decoupled Payload Packaging (Metadata vs. Avatar Media)

To strictly enforce the **64 KB maximum payload limit** of [`InboundIngressPipeline.cs`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Ingress/InboundIngressPipeline.cs#L14) while allowing high-resolution avatar photos, the profile system decouples text metadata from media blobs:

### 3.1 Compact Profile Metadata Package (Fixed 1024-Byte Block)
* **Plaintext Structure**:
  $$\text{MetadataBytes} = [\text{Revision (4B)}][\text{DisplayName (2B len + UTF-8)}][\text{StatusBio (2B len + UTF-8)}][\text{AvatarRefLen (2B)}][\text{AvatarBlobReferenceBytes}]$$
  * `AvatarBlobReferenceBytes`: A serialized [`BlobReference`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.PluginSdk/BlobStorageMode.cs) (or empty bytes if no avatar is set).
* **Constant-Size Bucket Padding (Signal Parity)**:
  To prevent the Relay from fingerprinting user identities via display name length, bio length, or avatar presence, `MetadataBytes` is padded using ISO/IEC 7816-4 padding to **exactly 1024 bytes** prior to AEAD encryption:
  $$\text{PaddedBytes} = \text{PadToConstantLength}(\text{MetadataBytes}, 1024)$$
* **AEAD Encryption**:
  $$\text{Ciphertext} = \text{AES-256-GCM}_{\text{Encrypt}}(K = \text{ProfileKey}, N = \text{Nonce (12B)}, P = \text{PaddedBytes}, AD = \text{Revision (4B)})$$
  * *Replay / Tamper Defense*: Binding `Revision` into AEAD Associated Data ($AD$) guarantees ciphertext integrity against rollback attacks.
* **Domain Model**: Encapsulated in `ProfileCiphertextPackage(byte[] Nonce, byte[] Ciphertext)` within `Percolator.Domain.Security.ValueObjects`. Every user's ciphertext package is exactly $1024 + 16\text{ (GCM tag)} = 1040\text{ bytes}$, presenting a uniform signature to untrusted hosts.

### 3.2 Avatar Image Blob (Channel-Independent Out-of-Band Media)
* **Metadata Minimization**: The raw image is EXIF-scrubbed, bucket-padded, and encrypted using chunked STREAM AEAD (64 KB chunks) under `AvatarKey`.
* **Channel-Independent Storage**: Avatar blobs are uploaded without association to any `ChannelId`. The relay indexes the blob purely by its content hash `BlobId`:
  * **Relayed Mode**: Uploaded via unauthenticated or identity-hosted blob storage (`StorageMode.PublicRelayBlob`). The relay has zero knowledge of which conversation or user identity the avatar belongs to.
  * **Direct 1:1 P2P Mode**: Staged in local chunk storage (`ILocalChunkStore`). The resulting `BlobReference` records `StorageMode = BlobStorageMode.DirectP2P`.

---

## 4. Wire Protocol & Chat Sub-Message Integration (`Percolator.Apps.Chat`)

Profile capability exchange operates within the Chat subsystem (`AppId.Chat = 0x01`), adhering to traffic-analysis padding and channel boundaries.

### 4.1 Sub-Message Type Discriminator
Add `ProfileCapability = 9` to [`ChatPayloadType`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.Chat/Features/Messaging/ChatPayloadType.cs):

```csharp
namespace Percolator.Apps.Chat.Features.Messaging;

public enum ChatPayloadType : byte
{
    Message = 1,
    Edit = 2,
    Tombstone = 3,
    Reaction = 4,
    ReadReceipt = 5,
    SystemNotice = 6,
    GroupInvitation = 7,
    GroupInvitationResponse = 8,
    ProfileCapability = 9 // Capability token distributing ProfileKey, locator, and mailbox token
}
```

### 4.2 Capability Wire DTO
Define `ProfileCapabilityDto` within `Percolator.Apps.Chat.Features.Profiles`:

```csharp
namespace Percolator.Apps.Chat.Features.Profiles;

public sealed record ProfileCapabilityDto(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    byte[] ProfileKeyBytes,
    byte[] BlindedLocatorBytes,
    byte[] MailboxRoutingTokenBytes, // Alice's BlindedRoutingToken enabling Charlie to route sealed envelopes to Alice
    uint Revision,
    ulong TimestampUnixMs);
```

### 4.3 Framing & Padding
When dispatched, `ProfileCapabilityDto` is serialized by `IPayloadSerializer` and wrapped in [`ChatPayloadEnvelopeDto`](file:///C:/Users/squir/source/repos/percolator/source/Percolator.Apps.Chat/Features/Messaging/ChatPayloadEnvelopeDto.cs):
* Wrapped with `ChatPayloadType.ProfileCapability`.
* Padded to discreet bucket sizes (256, 512, 1024 bytes) via `ChatPayloadEnvelopeDto.Padding`.
* Encrypted end-to-end under the channel's Double Ratchet or Sender Key session.
* Transmitted inside `SealedEnvelopeProto` using recipient `DeliveryToken` and `BlindedRoutingToken`.

---

## 5. End-to-End Execution Flows

### 5.1 Topology 1: Relayed Channel (Store-and-Forward Relay)

```
Alice                                Relay Node                                Charlie
  │                                      │                                        │
  │ 1. Upload Encrypted Profile          │                                        │
  │    (Locator, WriteToken, Rev, Pkg)   │                                        │
  │    [Unauth Ingress / Invariant Check]│                                        │
  │─────────────────────────────────────>│                                        │
  │                                      │                                        │
  │ 2. Distribute Capability via Chat    │                                        │
  │    (ProfileKey, Locator, MboxToken)  │                                        │
  │    [Enqueued on Relay Mailbox]       │                                        │
  │─────────────────────────────────────>│                                        │
  │                                      │ 3. Drain Mailbox                       │
  │                                      │    [Receives ProfileCapabilityDto]     │
  │                                      │<───────────────────────────────────────│
  │                                      │                                        │
  │                                      │ 4. Unauthenticated Profile Fetch       │
  │                                      │    (rpc GetProfile(Locator))           │
  │                                      │    [Zero Sender Identity Metadata]     │
  │                                      │<───────────────────────────────────────│
  │                                      │ 5. Returns CiphertextPackage           │
  │                                      │───────────────────────────────────────>│
  │                                      │                                        │
  │                                      │                    6. Decrypt Package; │
  │                                      │                       Update Profile;  │
  │                                      │                       Fetch Avatar     │
```

#### Step 1: Owner Profile Publication & Relay Aggregate Validation
1. Alice updates her profile text (`DisplayName = "Alice"`, `StatusBio = "Cypherpunk"`).
2. If an avatar photo is selected, Alice uploads the encrypted avatar blob to the relay via channel-independent blob upload, obtaining `AvatarBlobReference`.
3. Alice packs the metadata and `AvatarBlobReference`, pads to 1024 bytes, and encrypts it with `ProfileKey` via `IProfileManager.EncryptProfile` with `Revision` as associated data ($AD$), yielding `ProfileCiphertextPackage`.
4. Alice computes `BlindedProfileLocator` and `ProfileWriteToken`.
5. Alice calls `UnauthenticatedDeliveryService.UploadProfile(locator, writeToken, revision, package)`.
   * The Relay loads or instantiates `RelayHostedProfileSlot` from `IRelayProfileRepository`.
   * The aggregate executes `slot.Update(writeToken, package, revision, expiresAt)` (or creates a fresh slot).
   * **Invariants Enforced**:
     - Slot package size must not exceed 1040 bytes ($1024\text{ B payload} + 16\text{ B tag}$).
     - If slot exists, `writeToken` must match existing token (`UNAUTHORIZED_PROFILE_WRITE`).
     - `revision` must be strictly greater than existing revision (rollback protection).
     - Expiry timestamp is renewed.

#### Step 2: Capability Exchange over Relay Mailbox
1. Alice calls `IChatService.ShareProfileCapabilityAsync(channelId, aliceId, charlieId, DeliveryRoute.RelayedOneToOne)`.
2. Alice's outbox worker encrypts `ProfileCapabilityDto` under her pairwise `DirectRatchetSession` with Charlie and enqueues the sealed envelope on `Relay.MailboxQueue`.

#### Step 3: Recipient Retrieval, Version Check & Anonymous Fetch
1. Charlie drains `Relay.MailboxQueue` via `DrainMailboxAsync`.
2. Charlie's `InboundIngressPipeline` unwraps the envelope and dispatches to `ChatPayloadHandler`.
3. `ChatPayloadHandler.HandleProfileCapabilityAsync` extracts `ProfileKeyBytes`, `BlindedLocatorBytes`, `MailboxRoutingTokenBytes`, and `Revision`.
4. **Conditional Fetch**: Charlie checks his local profile cache (`IPeerProfileRepository.GetByPeerIdAsync(aliceId)`).
   * If `cachedProfile != null && cachedProfile.Revision >= dto.Revision`, Charlie ignores the fetch, eliminating redundant network roundtrips.
5. If `Revision` is newer:
   * Charlie derives Alice's `DeliveryToken` from `dto.ProfileKeyBytes`.
   * Charlie saves Alice's delivery tuple `(MailboxRoutingToken, DeliveryToken)` in his local routing store, authorizing future message delivery to Alice.
   * **Anonymous Capability Fetch**: Charlie executes an **unauthenticated gRPC call** to the Relay:
     `UnauthenticatedDeliveryService.GetProfile(locator)`
     *Charlie does NOT send his `PublicIdentityId`, TLS client certificate, or session auth token.*
   * The Relay looks up the slot by `BlindedProfileLocator` via `IRelayProfileRepository.GetSlotAsync` and returns `ProfileCiphertextPackage`. The Relay learns **nothing** about Charlie's social graph.
   * Charlie decrypts `ProfileCiphertextPackage` using `ProfileKey` and validates `Revision`.
   * Charlie updates Alice's profile record in `IPeerProfileRepository`. **Note**: Charlie's local custom nickname in `PeerContact.Nickname` is preserved and not overwritten.
   * If `AvatarBlobReference` is present and updated, Charlie fetches the avatar ciphertext anonymously from the relay blob store via `UnauthenticatedDeliveryService.DownloadBlindBlob(blobId)`.

---

### 5.2 Topology 2: Direct 1:1 P2P Channel (Serverless Direct gRPC Ingress)

Because every Percolator peer node runs `UnauthenticatedDeliveryService` on its local ephemeral listening port to receive direct messages (`DeliverDirect`), direct peer profile retrieval uses the **identical gRPC interface** as the Relay:

```
Alice (Listening on :15001)                                     Bob (Listening on :15002)
  │                                                                           │
  │ 1. Direct P2P Connected over Ephemeral TLS                                │
  │<═════════════════════════════════════════════════════════════════════════>│
  │                                                                           │
  │ 2. Distribute Capability inside E2EE Chat Stream                          │
  │    (ProfileKey, Locator, MboxToken, Rev: 1)                               │
  │──────────────────────────────────────────────────────────────────────────>│
  │                                                                           │
  │ 3. Unauthenticated gRPC Profile Fetch to Alice's Port                     │
  │    (rpc GetProfile(Locator) -> Alice :15001)                              │
  │<──────────────────────────────────────────────────────────────────────────│
  │ 4. Alice returns CiphertextPackage                                        │
  │──────────────────────────────────────────────────────────────────────────>│
  │                                                                           │
  │ 5. (If Avatar Present) Stream Avatar Chunks                               │
  │    (rpc DownloadBlindBlob(BlobId) -> Alice :15001)                        │
  │<──────────────────────────────────────────────────────────────────────────│
  │    Alice streams BlindBlobChunkProto                                      │
  │──────────────────────────────────────────────────────────────────────────>│
  │                                                                           │
  │                                                        6. Decrypt Package;│
  │                                                           Update Profile; │
  │                                                           Decrypt Avatar  │
```

#### Step 1: Local Staging
1. Alice encrypts `ProfileCiphertextPackage` (padded to 1024 B) and stages it in her local `RelayHostedProfileSlot` in `SqliteRelayProfileRepository`.
2. If an avatar photo is configured, Alice stages the avatar chunks in `ILocalChunkStore` under `BlobStorageMode.DirectP2P`.

#### Step 2: Capability Distribution
1. Alice calls `IChatService.ShareProfileCapabilityAsync(channelId, aliceId, bobId, DeliveryRoute.DirectP2P)`.
2. The `ProfileCapabilityDto` is transmitted inside the established Double Ratchet encrypted session over the direct TLS connection.

#### Step 3: Direct gRPC Profile Pull (Zero Custom Framing)
1. Bob's `ChatPayloadHandler` receives `ProfileCapabilityDto`.
2. If `dto.Revision > localCachedRevision`, Bob invokes the standard gRPC RPC directly against Alice's peer endpoint:
   `UnauthenticatedDeliveryService.GetProfile(locator)`
3. Alice's local `UnauthenticatedDeliveryEndpoint` looks up the slot by `BlindedProfileLocator` and returns `ProfileCiphertextPackage`.
4. Bob decrypts the package with `ProfileKey` and updates `IPeerProfileRepository`.
5. If an avatar `BlobReference` is present, Bob streams the image chunks directly from Alice using `UnauthenticatedDeliveryService.DownloadBlindBlob(blobId)`.

---

## 6. Key Rotation & Forward Access Revocation

Following the Signal security model, profile access revocation is achieved via **Key Rotation**:

```
[ Alice Blocks Malicious Contact Bob ]
                │
                ▼
1. Rotate ProfileKey: Generate fresh ProfileKey'
2. Derive new operational tokens:
   ├── BlindedProfileLocator' (New storage address)
   ├── ProfileWriteToken'     (New write token)
   ├── DeliveryToken'         (New mailbox delivery token)
   └── AvatarKey'             (New avatar encryption key)
3. Re-encrypt Profile Metadata & Avatar with ProfileKey' (padded to 1024 B)
4. Upload BlindedProfileLocator' to Relay via UploadProfile
5. Delete old BlindedProfileLocator from Relay via DeleteProfile(oldLocator, oldWriteToken)
6. Update Alice's Relay Mailbox to require DeliveryToken'
7. Distribute ProfileKey' ONLY to remaining approved contacts (Charlie, Dave)
                │
                ▼
Result: Bob retains only stale ProfileKey.
├── Cannot locate or fetch Alice's new profile on Relay (address changed).
├── Old profile slot is deleted (returns NOT_FOUND).
└── Cannot drop messages into Alice's mailbox (DeliveryToken mismatch).
```

---

## 7. Domain & Application Port Specifications

### 7.1 Relay Profile Hosting Aggregate & Port (`Percolator.Domain.Delivery`)

```csharp
namespace Percolator.Domain.Delivery.Hosting;

public sealed class RelayHostedProfileSlot : AggregateRoot<BlindedProfileLocator>
{
    public const int MaxPackageSizeBytes = 1040; // 1024 B padded payload + 16 B AEAD tag
    public override BlindedProfileLocator Id { get; }
    public ProfileWriteToken WriteToken { get; }
    public ProfileCiphertextPackage Package { get; private set; }
    public uint Revision { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public RelayHostedProfileSlot(
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        if (package.Ciphertext.Length > MaxPackageSizeBytes)
        {
            throw new ArgumentException($"Package size {package.Ciphertext.Length} exceeds limit of {MaxPackageSizeBytes} bytes.", nameof(package));
        }

        Id = locator;
        WriteToken = writeToken;
        Package = package;
        Revision = revision;
        ExpiresAtUtc = expiresAtUtc;
    }

    public static DomainResult<RelayHostedProfileSlot> Create(
        BlindedProfileLocator locator,
        ProfileWriteToken writeToken,
        ProfileCiphertextPackage package,
        uint revision,
        DateTimeOffset expiresAtUtc)
    {
        if (package.Ciphertext.Length > MaxPackageSizeBytes)
        {
            return DomainResult<RelayHostedProfileSlot>.Failure(
                new DomainError("PACKAGE_TOO_LARGE", $"Profile package size of {package.Ciphertext.Length} bytes exceeds limit of {MaxPackageSizeBytes} bytes."));
        }

        return DomainResult<RelayHostedProfileSlot>.Success(
            new RelayHostedProfileSlot(locator, writeToken, package, revision, expiresAtUtc));
    }

    public DomainResult Update(
        ProfileWriteToken presentedToken,
        ProfileCiphertextPackage newPackage,
        uint newRevision,
        DateTimeOffset newExpiresAtUtc)
    {
        if (newPackage.Ciphertext.Length > MaxPackageSizeBytes)
        {
            return DomainResult.Failure(
                new DomainError("PACKAGE_TOO_LARGE", $"Profile package size of {newPackage.Ciphertext.Length} bytes exceeds limit of {MaxPackageSizeBytes} bytes."));
        }

        if (WriteToken != presentedToken)
        {
            return DomainResult.Failure(new DomainError("UNAUTHORIZED_PROFILE_WRITE", "Invalid write token for profile slot."));
        }

        if (newRevision <= Revision)
        {
            return DomainResult.Failure(new DomainError("STALE_PROFILE_REVISION", "New profile revision must be strictly greater than current revision."));
        }

        Package = newPackage;
        Revision = newRevision;
        ExpiresAtUtc = newExpiresAtUtc;
        return DomainResult.Success();
    }

    public DomainResult<ProfileCiphertextPackage> TryGet(IDateTimeProvider timeProvider)
    {
        if (ExpiresAtUtc <= timeProvider.UtcNow)
        {
            return DomainResult<ProfileCiphertextPackage>.Failure(new DomainError("PROFILE_EXPIRED", "Profile slot has expired."));
        }

        return DomainResult<ProfileCiphertextPackage>.Success(Package);
    }
}
```

```csharp
namespace Percolator.Domain.Delivery.Ports;

public interface IRelayProfileRepository
{
    ValueTask<RelayHostedProfileSlot?> GetSlotAsync(BlindedProfileLocator locator, CancellationToken ct = default);
    ValueTask<DomainResult> SaveSlotAsync(RelayHostedProfileSlot slot, CancellationToken ct = default);
    ValueTask<DomainResult> DeleteSlotAsync(BlindedProfileLocator locator, ProfileWriteToken writeToken, CancellationToken ct = default);
}
```

### 7.2 Cryptographic Value Objects (`Percolator.Domain.Security.ValueObjects` & `Delivery.ValueObjects`)

```csharp
namespace Percolator.Domain.Security.ValueObjects;

public sealed class ProfileKey : IDisposable
{
    public const int Length = 32;
    private readonly byte[] _keyBytes;
    public ReadOnlySpan<byte> Span => _keyBytes;

    public ProfileKey(byte[] keyBytes)
    {
        ArgumentNullException.ThrowIfNull(keyBytes);
        if (keyBytes.Length != Length)
        {
            throw new ArgumentException($"Profile key must be {Length} bytes.", nameof(keyBytes));
        }
        _keyBytes = (byte[])keyBytes.Clone();
    }

    public static ProfileKey Generate()
    {
        var bytes = new byte[Length];
        RandomNumberGenerator.Fill(bytes);
        return new ProfileKey(bytes);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_keyBytes);
    }
}

public readonly record struct ProfileCiphertextPackage(byte[] Nonce, byte[] Ciphertext);
```

```csharp
namespace Percolator.Domain.Delivery.ValueObjects;

public readonly record struct BlindedProfileLocator
{
    public const int Length = 32;
    private readonly byte[] _bytes;
    public ReadOnlySpan<byte> Span => _bytes;

    public BlindedProfileLocator(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"BlindedProfileLocator must be exactly {Length} bytes.", nameof(bytes));
        }
        _bytes = (byte[])bytes.Clone();
    }

    public static BlindedProfileLocator Derive(
        IdentityKey ownerPublicKey,
        ProfileKey profileKey,
        ICryptoEngine cryptoEngine)
    {
        var locatorBytes = cryptoEngine.HkdfSha256(
            inputKeyMaterial: ownerPublicKey.Span,
            salt: profileKey.Span,
            info: System.Text.Encoding.UTF8.GetBytes("PercolatorProfileLocatorV2"),
            outputLength: Length);

        return new BlindedProfileLocator(locatorBytes);
    }
}

public readonly record struct ProfileWriteToken
{
    public const int Length = 32;
    private readonly byte[] _bytes;
    public ReadOnlySpan<byte> Span => _bytes;

    public ProfileWriteToken(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != Length)
        {
            throw new ArgumentException($"ProfileWriteToken must be exactly {Length} bytes.", nameof(bytes));
        }
        _bytes = (byte[])bytes.Clone();
    }

    public static ProfileWriteToken Derive(
        IdentityKey ownerPublicKey,
        ProfileKey profileKey,
        ICryptoEngine cryptoEngine)
    {
        var tokenBytes = cryptoEngine.HkdfSha256(
            inputKeyMaterial: ownerPublicKey.Span,
            salt: profileKey.Span,
            info: System.Text.Encoding.UTF8.GetBytes("PercolatorProfileWriteTokenV1"),
            outputLength: Length);

        return new ProfileWriteToken(tokenBytes);
    }
}
```

### 7.3 Peer Profile Persistence & CQRS Read Models (`Percolator.Application.Ports`)

```csharp
namespace Percolator.Application2.Ports;

public sealed record PeerProfileRecord(
    PublicIdentityId PeerId,
    string DisplayName,
    string? StatusBio,
    BlobReference? AvatarReference,
    uint Revision,
    DateTimeOffset UpdatedAtUtc);

public interface IPeerProfileRepository
{
    Task SaveAsync(PeerProfileRecord record, CancellationToken ct = default);
    Task<PeerProfileRecord?> GetByPeerIdAsync(PublicIdentityId peerId, CancellationToken ct = default);
}

public sealed record PeerProfileReadModel(
    PublicIdentityId PeerId,
    string DisplayName,
    string? StatusBio,
    bool HasAvatar,
    uint Revision,
    DateTimeOffset UpdatedAtUtc);

public interface IPeerProfileQueryService
{
    Task<PeerProfileReadModel?> GetProfileAsync(PublicIdentityId peerId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<PublicIdentityId, PeerProfileReadModel>> GetProfilesAsync(
        IEnumerable<PublicIdentityId> peerIds, CancellationToken ct = default);
}
```

* **UI Display Resolution Hierarchy**:
  $$\text{EffectiveName} = \text{PeerContact.Nickname} \mathrel{??} \text{PeerProfileRecord.DisplayName} \mathrel{??} \text{PublicIdentityId.ToShortString()}$$
  This cleanly respects local user nicknames without allowing remote profile updates to overwrite user preferences.

---

## 8. Unauthenticated gRPC Ingress Specification (`Percolator.Infrastructure2`)

To uphold Signal's zero-knowledge threat model on the wire, profile querying, blind blob retrieval, and profile slot deletion are integrated into the unauthenticated public edge service (`unauthenticated_delivery.proto`):

### 8.1 Protobuf Contract Updates (`unauthenticated_delivery.proto`)

```protobuf
syntax = "proto3";

package percolator.infrastructure;

import "google/protobuf/timestamp.proto";

service UnauthenticatedDeliveryService {
    // Sealed sender envelope ingress
    rpc DeliverDirect (SealedEnvelopeProto) returns (DeliveryAckProto);
    rpc EnqueueMailbox (SealedEnvelopeProto) returns (EnqueueAckProto);

    // Unauthenticated profile hosting ingress
    rpc GetProfile (ProfileFetchRequestProto) returns (ProfileFetchResponseProto);
    rpc UploadProfile (ProfileUploadRequestProto) returns (ProfileUploadResponseProto);
    rpc DeleteProfile (ProfileDeleteRequestProto) returns (ProfileDeleteResponseProto);

    // Channel-independent blind media blob retrieval
    rpc DownloadBlindBlob (BlindBlobDownloadRequestProto) returns (stream BlindBlobChunkProto);
}

message ProfileFetchRequestProto {
    bytes blinded_locator = 1; // 32-byte BlindedProfileLocator (No caller identity metadata)
}

message ProfileFetchResponseProto {
    bool found = 1;
    bytes nonce = 2;           // 12-byte AEAD Nonce
    bytes ciphertext = 3;      // AEAD CiphertextPackage (fixed 1040-byte padded block)
    uint32 revision = 4;
    google.protobuf.Timestamp expires_at_utc = 5;
}

message ProfileUploadRequestProto {
    bytes blinded_locator = 1;     // 32-byte BlindedProfileLocator
    bytes write_token = 2;         // 32-byte ProfileWriteToken
    uint32 revision = 3;           // Monotonic revision
    bytes nonce = 4;
    bytes ciphertext = 5;          // AEAD CiphertextPackage (fixed 1040-byte padded block)
    google.protobuf.Timestamp expires_at_utc = 6;
}

message ProfileUploadResponseProto {
    bool success = 1;
    string error_message = 2;
}

message ProfileDeleteRequestProto {
    bytes blinded_locator = 1;     // 32-byte BlindedProfileLocator
    bytes write_token = 2;         // 32-byte ProfileWriteToken
}

message ProfileDeleteResponseProto {
    bool success = 1;
    string error_message = 2;
}

message BlindBlobDownloadRequestProto {
    bytes blob_id = 1;             // 32-byte content hash (No ChannelId)
}

message BlindBlobChunkProto {
    bytes chunk_data = 1;
    uint32 chunk_index = 2;
    bool is_final_chunk = 3;
}
```

---

## 9. Implementation Checklist & Verification Matrix

| Layer | Component | Task |
| :--- | :--- | :--- |
| **Domain** | `ProfileKey` & `ProfileCiphertextPackage` | Define 32-byte disposable key and AEAD package in `Percolator.Domain.Security.ValueObjects`. |
| **Domain** | `BlindedProfileLocator` & `ProfileWriteToken` | Implement 32-byte value objects with HKDF derivation in `Percolator.Domain.Delivery.ValueObjects`. |
| **Domain** | `DeliveryToken` | Wire `DeliveryToken.DeriveFromProfileKey(profileKey, cryptoEngine)` matching Signal Sealed Sender. |
| **Domain** | `RelayHostedProfileSlot` | Implement aggregate root with write-token check, monotonic revisioning, 1040 B quota, and TTL. |
| **Domain** | `IRelayProfileRepository` | Define slot-based storage port (`GetSlotAsync`, `SaveSlotAsync`, `DeleteSlotAsync`). |
| **Application** | `IPeerProfileRepository` | Define client-side persistent storage for decrypted peer profiles. |
| **Application** | `IPeerProfileQueryService` | Implement CQRS read-model query port for UI / test verification. |
| **Apps.Chat** | `ChatPayloadType` | Add `ProfileCapability = 9` to enum. |
| **Apps.Chat** | `ProfileCapabilityDto` | Define wire DTO carrying `(ChannelId, AuthorId, ProfileKey, Locator, MailboxToken, Revision)`. |
| **Apps.Chat** | `IChatService` | Add `ShareProfileCapabilityAsync` dispatching padded envelope. |
| **Apps.Chat** | `ChatPayloadHandler` | Handle `ProfileCapability`, perform conditional fetch, decrypt via `IProfileManager`, and update profile store without overwriting local nickname. |
| **Infrastructure** | `unauthenticated_delivery.proto` | Add `GetProfile`, `UploadProfile`, `DeleteProfile`, and `DownloadBlindBlob` to `UnauthenticatedDeliveryService`. |
| **Infrastructure** | `UnauthenticatedDeliveryEndpoint` | Implement unauthenticated gRPC profile fetch, upload, delete, and blind blob download stubs on both Relay and Direct Peer endpoints. |
| **Integration** | Direct P2P Suite (Phase 1 Ext) | Assert Alice updates profile, shares capability over stream, Bob pulls & decrypts via direct `GetProfile`. |
| **Integration** | Relayed Suite (Phase 2 Ext) | Assert Alice publishes to Relay with write token, shares capability via mailbox, Charlie pulls anonymously & decrypts. |
| **Integration** | Key Rotation Suite | Assert Alice rotates `ProfileKey`, blocks Bob; Charlie decrypts update while Bob is rejected by Relay. |
