# Percolator.Application2 Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Application2` serves as the central coordination and use-case orchestration layer in the Percolator microkernel architecture. It coordinates the execution of application workflows, governs data ingress and egress pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees.

Following Clean Architecture and strict Onion Architecture principles:
- **Dependencies Flow Inward**: `Application2` references `Percolator.Domain` and `Percolator.PluginSdk`. It has zero dependencies on concrete infrastructure implementations (no gRPC, SQLite, EF Core, Protobuf, or socket APIs).
- **Application Payload Opacity**: Decrypts and authenticates transport envelopes using domain crypto models, then dispatches opaque inner byte buffers to registered application handlers (`Chat`, `Discovery`, `FileTransfer`) via constant-time `AppId` lookup.
- **Always Outbox First**: All outbound envelopes are persisted atomically to `IOutboxRepository` before transmission is attempted over network streams or relay queues.
- **CQRS Separation (Domain Mutation vs. Read-Only Queries)**:
  - **Write Operations (Commands & State Transitions)**: Enforce domain invariants through domain aggregates (`DirectRatchetSession`, `GroupSenderKeyRatchet`, `OutboxJob`, `PeerContact`) and persist through aggregate repositories.
  - **Read Operations (Queries & UI Projections)**: Read-only workflows (such as UI contact books, outbox status monitors, or transaction histories) do **not** hydrate heavy domain aggregates with encrypted payloads and event queues. Instead, they query database tables directly through specialized query ports (`IOutboxQueryService`, `IPeerContactQueryService`) returning lightweight, flat DTOs.
- **Zero Wire-Protocol Logic**: `Application2` does not define, parse, or slice physical network wire formats (Protobuf, gRPC, or custom binary byte offsets). Physical wire framing, transport serialization, and network sockets live strictly in `Percolator.Infrastructure2`. Wire-to-domain envelope translation is handled via the `ISessionWirePacker` port.
- **Cryptographic Logging Guardrails**: Respects `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default. Diagnostic traces and loggers must never record root keys, chain keys, message keys, ephemeral private keys, or decrypted plaintexts.

---

## 2. Ingress & Egress Pipeline Architecture

`Application2` operates entirely on strongly-typed C# cryptographic envelopes and Domain models:

### 2.1 Inbound Ingress Pipeline (`IInboundIngressPipeline`)
Accepts typed incoming envelopes instantiated by Infrastructure transport listeners:

```csharp
public abstract record InboundEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    DateTimeOffset ReceivedAtUtc);

public sealed record InboundDirectEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    RatchetHeader Header,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(ChannelId, RecipientIdentityId, SenderIdentityId, SenderDeviceId, ReceivedAtUtc);

public sealed record InboundGroupEnvelope(
    ChannelId ChannelId,
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId AuthorIdentityId,
    DeviceId AuthorDeviceId,
    uint Iteration,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> Signature,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(ChannelId, RecipientIdentityId, AuthorIdentityId, AuthorDeviceId, ReceivedAtUtc);

public sealed record InboundHandshakeEnvelope(
    PublicIdentityId RecipientIdentityId,
    PublicIdentityId SenderIdentityId,
    DeviceId SenderDeviceId,
    IdentityKey SenderIdentityKey,
    DhPublicKey SenderEphemeralKey,
    uint SignedPreKeyId,
    uint? OneTimePreKeyId,
    ReadOnlyMemory<byte> EncryptedPayload,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(new ChannelId(), RecipientIdentityId, SenderIdentityId, SenderDeviceId, ReceivedAtUtc);
```

#### Pipeline Phases:
1. **Size & Sanity Validation**: Rejects payloads exceeding maximum allowed size (`MaxPayloadBytes = 64 KB`).
2. **Ingress Filtering**: Invokes `IIngressFilterService.CheckIngressAllowedAsync` (rate limiting, blocked peer checks, dormancy state).
3. **Ratchet Concurrency Lock**: Acquires scoped channel/session synchronization via `IChannelLockService` to prevent concurrent ratchet modifications.
4. **Cryptographic Processing by Envelope Type**:
   - **`InboundHandshakeEnvelope`**: Delegated to `IHandshakeService.ReceiveInvitationAsync`.
   - **`InboundDirectEnvelope`**:
     - Fetches active session from `IRatchetSessionRepository`.
     - **DH Ratchet Advancement**: If `session.RemoteEphemeralPublicKey != envelope.Header.EphemeralPublicKey`, advances ratchet via `session.StepDhRatchet(envelope.Header.EphemeralPublicKey, _cryptoEngine)`.
     - Steps receiving chain: `session.StepReceivingChain(_cryptoEngine, envelope.Header.Counter)`.
     - Decrypts ciphertext via `_cryptoEngine.DecryptAesGcm` using derived message key, `envelope.Nonce`, and header bytes as associated data.
     - Persists advanced session to `IRatchetSessionRepository`.
   - **`InboundGroupEnvelope`**:
     - Fetches `GroupReceiverSession` from `IGroupReceiverSessionRepository`.
     - Advances iteration: `receiverSession.TryAdvanceToIteration(envelope.Iteration, _cryptoEngine)` $\rightarrow$ derives message key.
     - Decrypts ciphertext via `_cryptoEngine.DecryptAesGcm`.
     - Verifies author signature: `receiverSession.VerifyAuthorSignature(envelope.Ciphertext, envelope.Signature, _cryptoEngine)`.
     - Persists advanced receiver session to `IGroupReceiverSessionRepository`.
5. **AppRouter Direct Jump**:
   - Reads `AppId` from byte 0 of decrypted inner plaintext (`[0] = AppId, [1..] = Payload`).
   - If `AppId` corresponds to internal system messages (e.g. `AppId.System` for Sender Key Distribution), routes to internal system coordinators.
   - Otherwise, resolves handler via `_appRouter.Resolve(appId)` and dispatches `InboundPayloadContext`.
6. **Deterministic Plaintext Zeroization**: Plaintext memory buffer is zeroized (`Array.Clear` / pooled buffer return) in a `finally` block before returning.

---

### 2.2 Outbound Egress Pipeline (`IPayloadSender`)
Accepts an `OutboundPayloadContext` from plugins via `PluginSdk`:

#### Pipeline Phases:
1. **Ratchet Concurrency Lock**: Acquires scoped channel lock via `IChannelLockService` to prevent concurrent sends on the same session from interleaving ratchet keys.
2. **Inner Payload Packing**: Encapsulates `[AppId] + [Payload]`.
3. **Cryptographic Ratchet Stepping & Encryption**:
   - **Pairwise 1:1 (`context.RecipientIdentityId.HasValue`)**:
     - Fetches session from `IRatchetSessionRepository`.
     - Advances sending chain: `session.StepSendingChain(_cryptoEngine)` $\rightarrow$ yields counter, message key, ephemeral public key.
     - Constructs `RatchetHeader` with ephemeral key, counter, and previous chain length.
     - Encrypts inner payload with `_cryptoEngine.EncryptAesGcm(messageKey, nonce, innerPlaintext, headerBytes)`.
     - Invokes `ISessionWirePacker.PackDirectRatchetMessage(header, nonce, ciphertext)` $\rightarrow$ serializes to self-contained wire bytes.
     - Persists advanced session to `IRatchetSessionRepository`.
   - **Group Broadcast (`context.RecipientIdentityId == null`)**:
     - Fetches `GroupSenderKeyRatchet` from `IGroupSenderKeyRepository`.
     - Advances ratchet: `ratchet.Advance(_cryptoEngine)` $\rightarrow$ yields iteration and message key.
     - Encrypts inner payload with `_cryptoEngine.EncryptAesGcm`.
     - Signs ciphertext: `ratchet.SignPayload(ciphertext, _cryptoEngine)` $\rightarrow$ generates Ed25519 author signature.
     - Invokes `ISessionWirePacker.PackGroupMessage(channelId, iteration, signature, ciphertext)` $\rightarrow$ serializes to self-contained wire bytes.
     - Persists advanced ratchet to `IGroupSenderKeyRepository`.
4. **Route Resolution**: Resolves `DeliveryRoute` (`DirectP2P`, `RelayedOneToOne`, or `RelayedGroup`).
5. **Always Outbox First**: Creates `OutboxJob` with the packed wire bytes and saves to `IOutboxRepository`.
6. **Direct Stream Fast-Path**: If direct stream is active in `IStreamRegistry`, attempts immediate non-blocking dispatch; otherwise leaves job pending for `OutboxWorker`.

---

## 3. Handshake Orchestration (`IHandshakeService`)

Encapsulates X3DH key agreement and user consent workflows:

1. **`InitiateHandshakeAsync` (Outbound)**:
   - Takes remote peer `PreKeyBundle` and optional initial message payload.
   - Computes `X3dhAgreement.Initiate(...)`.
   - Instantiates outbound `DirectRatchetSession.CreateFromX3dhInitiator(...)`.
   - If initial message provided: steps sending chain, encrypts initial message.
   - Saves session to `IRatchetSessionRepository`.
   - Persists handshake outbox job to `IOutboxRepository`.

2. **`ReceiveInvitationAsync` (Inbound)**:
   - **Replay / DoS Filter**: Checks `IHandshakeReplayFilter` against `envelope.SenderEphemeralKey` to discard replayed handshake frames before computing expensive Curve25519 scalar multiplications.
   - Inspects `PeerContact` state via `IPeerContactRepository`:
     - **If Blocked**: silently drops invitation.
     - **If Untrusted / Not Found**: delegates to `IContactRequestCoordinator.HandleInboundRequestAsync(...)`, recording `ContactState.PendingApproval`.
       - If `envelope.EncryptedPayload` is non-empty, saves the raw handshake envelope to `IPendingHandshakeRepository` so the greeting message is preserved across user approval.
     - **If Active (`Tofu` or `Verified`)**:
       - Executes `DeriveSessionAndDispatchMessageAsync(...)`:
         - Looks up signed prekey and atomically consumes OPK from `IPrivatePreKeyStore`.
         - Computes `X3dhAgreement.Receive(...)` to derive master shared secret.
         - Instantiates inbound `DirectRatchetSession.CreateFromX3dhResponder(...)`.
         - Saves session to `IRatchetSessionRepository`.
         - If piggybacked initial message is present: steps receiving chain at counter 0, decrypts, and dispatches to `_appRouter`.

3. **`CompletePendingHandshakeAsync` (Approval Hook)**:
   - Invoked when user approves contact (`ContactRequestCoordinator.ApproveRequestAsync`).
   - Retrieves stored envelope from `IPendingHandshakeRepository`.
   - Executes session derivation, decrypts the deferred initial greeting message, and removes envelope from pending store.

---

## 4. Key Microkernel Architecture Enhancements

### 4.1 In-Process Channel/Session Synchronization (`IChannelLockService`)
- Ratchets are sequential state machines where concurrent operations lead to race conditions, key desynchronization, and corrupted sessions.
- `IChannelLockService` provides scoped asynchronous keyed locking (`AsyncKeyedLock<ChannelId>`) ensuring that only one outbound or inbound pipeline thread steps a given channel or session at any instant.
- **Design & Testability**:
  - Defined as an interface in `Percolator.Application2.Ports`.
  - Default implementation: `KeyedSemaphoreLockService` in `Application2`.
  - Test double: `NoOpChannelLockService` in `Percolator.Application2.Tests`, ensuring unit tests never deadlock on concurrent locks.

### 4.2 Handshake Ephemeral Key Cache (`IHandshakeReplayFilter`)
- Prevents CPU-exhaustion denial-of-service attacks by maintaining an in-memory bounded LRU / sliding window cache of recently observed `SenderEphemeralKey` public keys.
- Duplicate or replayed handshake frames are rejected immediately before consuming private prekeys or performing Diffie-Hellman scalar operations.
- **Design & Testability**:
  - Defined as an interface in `Percolator.Application2.Ports`.
  - Default implementation: `MemoryHandshakeReplayFilter` in `Application2`.
  - Test double: `FakeHandshakeReplayFilter` in `Percolator.Application2.Tests`.

### 4.3 Pending Handshake Repository (`IPendingHandshakeRepository`)
- Retains initial piggybacked greeting payloads during the `PendingApproval` contact phase.
- Once user consent is granted, the handshake derivation completes cleanly and dispatches the initial message without loss.
- **Design & Testability**:
  - True Infrastructure Port in `Percolator.Application2.Ports`.
  - Implemented in `Percolator.Infrastructure2` backed by SQLite/EF Core.

### 4.4 Group Sender Key Distribution Workflow (`IGroupKeyDistributionService`)
- **Signal Sender Keys Architecture**:
  - In a group channel, broadcast messages are encrypted with the author's local `GroupSenderKeyRatchet`.
  - In order for other group members to decrypt these messages, the author must distribute their sender key material (Chain Key + iteration + Ed25519 signing public key) to all group members **via pairwise 1:1 Double Ratchet channels**.
- **Orchestration**:
  - `IGroupKeyDistributionService`:
    ```csharp
    public interface IGroupKeyDistributionService
    {
        ValueTask<DomainResult> DistributeSenderKeyAsync(
            ChannelId channelId,
            PublicIdentityId senderId,
            IEnumerable<PublicIdentityId> recipientMemberIds,
            CancellationToken ct = default);
    }
    ```
  - Packages the sender key distribution message and sends it pairwise via `IPayloadSender.SendPayloadAsync(...)` with `AppId.System`.
  - On the receiving side, `InboundIngressPipeline` processes the incoming system message and imports the sender key into `IGroupReceiverSessionRepository`.

### 4.5 Plaintext Zeroization & Memory Pooling
- Protects against memory inspection and reduces GC churn for large payload traffic (such as 64 KB file chunks).
- Decrypted plaintexts are cleared from memory immediately upon dispatch completion in `finally` blocks.

---

## 5. Port & Service Organization

### 5.1 Infrastructure Ports (Implemented in `Percolator.Infrastructure2`)
These ports represent durable persistence, network wire packaging, and transport listeners:
1. **Domain Session Repositories**:
   - `IRatchetSessionRepository`
   - `IGroupReceiverSessionRepository`
   - `IGroupSenderKeyRepository`
   - `IPendingHandshakeRepository`
2. **Read-Only Fast-Path Query Ports (CQRS)**:
   - `IOutboxQueryService` (`Percolator.Application2.Delivery.Ports`): Flat `OutboxJobSummaryReadModel` reads.
   - `IPeerContactQueryService` (`Percolator.Application2.Ports`): Flat `PeerContactSummaryReadModel` / `PeerContactDetailReadModel` reads.
3. **Wire Packaging & Transport Serialization**:
   - `ISessionWirePacker`: Cryptographic wire frame packing and unpacking.
   - `ISealedEnvelopeUnwrapper`: Unwraps sealed mailbox envelopes to typed inbound envelopes.
4. **Environmental Stores & Sockets**:
   - `ILocalIdentityKeyStore`
   - `IPrivatePreKeyStore`
   - `IStreamRegistry`
   - `IOutboxRepository`
   - `ITransportDispatcher`
   - `IIngressFilterService`

### 5.2 Application-Internal Services & In-Library Defaults (Defined in `Percolator.Application2`)
These interfaces govern in-process orchestration and concurrency, providing in-library default implementations while remaining mockable for tests:
1. **`IChannelLockService`**: In-process asynchronous keyed locking (`KeyedSemaphoreLockService`).
2. **`IHandshakeReplayFilter`**: In-process ephemeral key sliding cache (`MemoryHandshakeReplayFilter`).
3. **`IGroupKeyDistributionService`**: Coordinates Signal Sender Key distribution across group members.
4. **`IHandshakeService`**: Orchestrates X3DH key agreement and pending contact hooks.
5. **`IInboundIngressPipeline`**: Orchestrates ingress size validation, filtering, decryption, and dispatch.
6. **`IPayloadSender` (`OutboundEgressPipeline`)**: Orchestrates outbound payload packing, ratcheting, wire framing, and outbox persistence.
7. **`IAppRouter`**: Microkernel registry and $O(1)$ constant-time payload dispatcher.
