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
    DhPublicKey SenderEphemeralKey,
    PreKeyId SignedPreKeyId,
    PreKeyId? OneTimePreKeyId,
    ReadOnlyMemory<byte> EncryptedPayload,
    DateTimeOffset ReceivedAtUtc)
    : InboundEnvelope(ChannelId.Empty, RecipientIdentityId, SenderIdentityId, SenderDeviceId, ReceivedAtUtc);
```

#### Pipeline Phases:
1. **Size & Sanity Validation**: Rejects payloads exceeding maximum allowed size (`MaxPayloadBytes = 64 KB`).
2. **Ingress Filtering**: Invokes `IIngressFilterService.CheckIngressAllowedAsync` (rate limiting, blocked peer checks, dormancy state).
3. **Cryptographic Processing by Envelope Type**:
   - **`InboundHandshakeEnvelope`**: Delegated to `IHandshakeService.ReceiveInvitationAsync`.
   - **`InboundDirectEnvelope`**:
     - Fetches active session from `IRatchetSessionRepository`.
     - **DH Ratchet Advancement**: If `session.RemoteEphemeralPublicKey != envelope.Header.RatchetKey`, advances ratchet via `session.StepDhRatchet(envelope.Header.RatchetKey, _cryptoEngine)`.
     - Steps receiving chain: `session.StepReceivingChain(_cryptoEngine, envelope.Header.Counter)`.
     - Decrypts ciphertext via `_cryptoEngine.DecryptAesGcm` using derived message key, `envelope.Nonce`, and header bytes as associated data.
     - Persists advanced session to `IRatchetSessionRepository`.
   - **`InboundGroupEnvelope`**:
     - Fetches `GroupReceiverSession` from `IGroupReceiverSessionRepository`.
     - Advances iteration: `receiverSession.TryAdvanceToIteration(envelope.Iteration, _cryptoEngine)` $\rightarrow$ derives message key.
     - Decrypts ciphertext via `_cryptoEngine.DecryptAesGcm`.
     - Verifies author signature: `receiverSession.VerifyAuthorSignature(envelope.Ciphertext, envelope.Signature, _cryptoEngine)`.
     - Persists advanced receiver session to `IGroupReceiverSessionRepository`.
4. **AppRouter Direct Jump**:
   - Reads `AppId` from byte 0 of decrypted inner plaintext (`[0] = AppId, [1..] = Payload`).
   - Resolves handler via `_appRouter.Resolve(appId)` and dispatches `InboundPayloadContext`.
5. **Post-Action Cleanup**: Deterministically zeroizes decrypted plaintext memory.

---

### 2.2 Outbound Egress Pipeline (`IPayloadSender`)
Accepts an `OutboundPayloadContext` from plugins via `PluginSdk`:

#### Pipeline Phases:
1. **Inner Payload Packing**: Encapsulates `[AppId] + [Payload]`.
2. **Cryptographic Ratchet Stepping & Encryption**:
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
3. **Route Resolution**: Resolves `DeliveryRoute` (`DirectP2P`, `RelayedOneToOne`, or `RelayedGroup`).
4. **Always Outbox First**: Creates `OutboxJob` with the packed wire bytes and saves to `IOutboxRepository`.
5. **Direct Stream Fast-Path**: If direct stream is active in `IStreamRegistry`, attempts immediate non-blocking dispatch; otherwise leaves job pending for `OutboxWorker`.

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
   - Inspects `PeerContact` state via `IPeerContactRepository`:
     - **If Blocked**: silently drops invitation.
     - **If Untrusted / Not Found**: delegates to `IContactRequestCoordinator.HandleInboundRequestAsync(...)`, recording `ContactState.PendingApproval`. Defers cryptographic session derivation until user consent/approval.
     - **If Active (`Tofu` or `Verified`)**:
       - Looks up signed prekey and atomically consumes OPK from `IPrivatePreKeyStore`.
       - Computes `X3dhAgreement.Receive(...)` to derive master shared secret.
       - Instantiates inbound `DirectRatchetSession.CreateFromX3dhResponder(...)`.
       - Saves session to `IRatchetSessionRepository`.
       - If piggybacked initial message is present: steps receiving chain at counter 0, decrypts, and dispatches to `_appRouter`.

---

## 4. Required Ports in `Percolator.Application2/Ports`

1. **Session Repositories (Domain Aggregates)**:
   - `IRatchetSessionRepository`:
     - `Task<DirectRatchetSession?> GetSessionAsync(PublicIdentityId ownerId, PublicIdentityId remotePeerId, DeviceId remoteDeviceId, CancellationToken ct = default);`
     - `Task SaveSessionAsync(DirectRatchetSession session, CancellationToken ct = default);`
   - `IGroupReceiverSessionRepository`:
     - `Task<GroupReceiverSession?> GetReceiverSessionAsync(ChannelId channelId, PublicIdentityId authorId, DeviceId authorDeviceId, CancellationToken ct = default);`
     - `Task SaveReceiverSessionAsync(GroupReceiverSession session, CancellationToken ct = default);`
   - `IGroupSenderKeyRepository`:
     - `Task<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(ChannelId channelId, PublicIdentityId authorId, DeviceId authorDeviceId, CancellationToken ct = default);`
     - `Task SaveSenderKeyRatchetAsync(GroupSenderKeyRatchet ratchet, CancellationToken ct = default);`

2. **Read-Only Fast-Path Query Ports (Bypassing Domain Aggregates)**:
   - `IOutboxQueryService` (`Percolator.Application2.Delivery.Ports`):
     - `Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetPendingJobsAsync(int limit = 50, CancellationToken ct = default);`
     - `Task<IReadOnlyList<OutboxJobSummaryReadModel>> GetFailedJobsAsync(int limit = 50, CancellationToken ct = default);`
     - `Task<OutboxJobSummaryReadModel?> GetJobSummaryByIdAsync(Guid jobId, CancellationToken ct = default);`
     - Returns lightweight DTOs without loading opaque encrypted payload buffers or event lists into memory.
   - `IPeerContactQueryService` (`Percolator.Application2.Ports`):
     - `Task<IReadOnlyList<PeerContactSummaryReadModel>> GetContactSummariesAsync(PublicIdentityId ownerId, CancellationToken ct = default);`
     - `Task<PeerContactDetailReadModel?> GetContactDetailAsync(PublicIdentityId ownerId, PublicIdentityId contactId, CancellationToken ct = default);`
     - Returns read-only contact book records for the UI without hydrating private keys or device link proofs.

3. **Wire Packaging & Serialization Port**:
   - `ISessionWirePacker`:
     ```csharp
     public interface ISessionWirePacker
     {
         ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> nonce, ReadOnlyMemory<byte> ciphertext);
         ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext);
         DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
         DomainResult<InboundGroupEnvelope> UnpackGroupMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId authorId, DeviceId authorDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
     }
     ```

4. **Services & Infrastructure Ports**:
   - `IHandshakeService`
   - `IIngressFilterService`
   - `IStreamRegistry`
   - `IOutboxRepository`
   - `ITransportDispatcher`
   - `ISealedEnvelopeUnwrapper` (bridges relay `MailboxEnvelope` to `InboundEnvelope`)
