# Percolator.Application2 Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Application2` serves as the central coordination and use-case orchestration layer in the Percolator microkernel architecture. It coordinates the execution of application workflows, governs data ingress and egress pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees.

Following Clean Architecture and strict Onion Architecture principles:
- **Dependencies Flow Inward**: `Application2` references `Percolator.Domain` and `Percolator.PluginSdk`. It has zero dependencies on concrete infrastructure implementations (no gRPC, SQLite, EF Core, or socket APIs).
- **Application Payload Opacity**: Decrypts and authenticates transport envelopes, then dispatches opaque inner byte buffers to registered application handlers (`Chat`, `Discovery`, `FileTransfer`) via constant-time `AppId` lookup.
- **Always Outbox First**: All outbound envelopes are persisted atomically to `IOutboxRepository` before transmission is attempted over direct network streams or relay queues.
- **Cryptographic Logging Guardrails**: Respects `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default. Diagnostic traces and loggers must never record root keys, chain keys, message keys, ephemeral private keys, or decrypted plaintexts.

---

## 2. Ingress & Egress Wire Framing

To support direct P2P, relayed 1:1, and relayed group messaging without protocol ambiguity or cross-frame confusion attacks, wire payloads are framed with a 1-byte discriminant:

- **`0x01` (`WireFrameType.HandshakeInvitation`)**:
  - `[0]`: Frame type discriminant (`0x01`)
  - `[1..32]`: `InitiatorIdentityKey` (32 bytes)
  - `[33..64]`: `InitiatorEphemeralPublicKey` (32 bytes)
  - `[65..68]`: `SignedPreKeyId` (4 bytes, Big-Endian uint)
  - `[69..72]`: `OneTimePreKeyId` (4 bytes, Big-Endian uint, 0 if unused)
  - `[73..]`: Optional Initial Ciphertext (`[73..84]`: Nonce (12B), `[85..^16]`: AES-256-GCM Ciphertext, `[^16..]`: Authentication Tag (16B)). Allows Alice to send her first message turn piggybacked directly onto the handshake.
  - Parsed by `HandshakeWireFrame.TryParse`.

- **`0x02` (`WireFrameType.RatchetMessage`)**:
  - `[0]`: Frame type discriminant (`0x02`)
  - `[1..32]`: `DhPublicKey` (32 bytes)
  - `[33..36]`: `MessageCounter` (4 bytes, Big-Endian uint)
  - `[37..40]`: `PreviousChainLength` (4 bytes, Big-Endian uint)
  - `[41..52]`: Nonce (12 bytes)
  - `[53..^16]`: AES-256-GCM Ciphertext
  - `[^16..]`: AES-256-GCM Authentication Tag (16 bytes)
  - Header size: 41 bytes (`[0..40]`). Header bytes are passed as Associated Data (AD) to `DecryptAesGcm` and `EncryptAesGcm` to cryptographically bind the frame type to the tag.
  - Parsed by `RatchetWireFrame.TryParse`.

- **`0x03` (`WireFrameType.GroupMessage`)**:
  - `[0]`: Frame type discriminant (`0x03`)
  - `[1..4]`: `KeyId` (4 bytes, Big-Endian uint)
  - `[5..8]`: `Iteration` (4 bytes, Big-Endian uint)
  - `[9..20]`: Nonce (12 bytes)
  - `[21..^(16+64)]`: AES-256-GCM Ciphertext
  - `[^(16+64)..^64]`: AES-256-GCM Authentication Tag (16 bytes)
  - `[^64..]`: Ed25519 Author Signature (64 bytes)
  - Header size: 21 bytes (`[0..20]`). Header bytes are passed as Associated Data (AD).
  - Parsed by `GroupWireFrame.TryParse`.

---

## 3. Milestones & Target Adapters

### Milestone 1: Ingress & Egress Wire Framing & Pipeline Execution

1. **Repository Ports**:
   - Define in `Percolator.Application2/Ports`:
     - `IGroupReceiverSessionRepository`:
       ```csharp
       ValueTask<GroupReceiverSession?> GetReceiverSessionAsync(
           ChannelId channelId,
           PublicIdentityId authorId,
           DeviceId authorDeviceId,
           CancellationToken ct = default);

       ValueTask SaveReceiverSessionAsync(
           GroupReceiverSession session,
           CancellationToken ct = default);
       ```
     - `IGroupSenderKeyRepository`:
       ```csharp
       ValueTask<GroupSenderKeyRatchet?> GetSenderKeyRatchetAsync(
           ChannelId channelId,
           PublicIdentityId authorId,
           DeviceId authorDeviceId,
           CancellationToken ct = default);

       ValueTask SaveSenderKeyRatchetAsync(
           GroupSenderKeyRatchet ratchet,
           CancellationToken ct = default);
       ```
   - Define `IHandshakeService` interface in `Percolator.Application2/Ports`:
     ```csharp
     public interface IHandshakeService
     {
         ValueTask<DomainResult> ReceiveInvitationAsync(
             InboundWireEnvelope envelope,
             HandshakeWireFrame frame,
             CancellationToken ct = default);

         ValueTask<DomainResult<DirectRatchetSession>> InitiateHandshakeAsync(
             PublicIdentityId ownerId,
             DeviceId ownerDeviceId,
             PreKeyBundle remoteBundle,
             ReadOnlyMemory<byte>? initialMessagePayload = null,
             CancellationToken ct = default);
     }
     ```

2. **Inbound Ingress Pipeline (`InboundIngressPipeline.cs`)**:
   - **Discriminant Inspection**: Read byte 0 of `envelope.WirePayload`.
   - **Frame `0x01` (`HandshakeInvitation`)**:
     - Parse `HandshakeWireFrame`.
     - Delegate to `_handshakeService.ReceiveInvitationAsync(envelope, frame, ct)`.
   - **Frame `0x02` (`RatchetMessage`)**:
     - Parse `RatchetWireFrame` (41-byte header).
     - Fetch session via `_sessionRepository.GetSessionAsync(...)`.
     - **DH Ratchet Advancement**: If `session.RemoteEphemeralPublicKey == null || session.RemoteEphemeralPublicKey != wireFrame.DhPublicKey`, execute `session.StepDhRatchet(wireFrame.DhPublicKey, _cryptoEngine)`.
     - Step receiving chain: `session.StepReceivingChain(_cryptoEngine, wireFrame.MessageCounter)`.
     - Decrypt AES-GCM using `headerBytes` (41 bytes) as Associated Data.
     - Persist updated session to `_sessionRepository.SaveSessionAsync(...)`.
     - Direct jump: dispatch decrypted `innerPlaintext` to `_appRouter.Resolve(appId)`.
     - Zeroize `innerPlaintext`.
   - **Frame `0x03` (`GroupMessage`)**:
     - Parse `GroupWireFrame`.
     - Fetch receiver session via `_groupReceiverRepo.GetReceiverSessionAsync(envelope.ChannelId, envelope.SenderIdentityId, envelope.SenderDeviceId, ct)`.
     - Advance iteration: `receiverSession.TryAdvanceToIteration(wireFrame.Iteration, _cryptoEngine)` $\rightarrow$ derives `MessageKey`.
     - Decrypt AES-GCM using `headerBytes` (21 bytes) as Associated Data.
     - Verify author signature: `receiverSession.VerifyAuthorSignature(ciphertext, signature, _cryptoEngine)`.
     - Persist updated receiver session to `_groupReceiverRepo.SaveReceiverSessionAsync(...)`.
     - Direct jump: dispatch decrypted `innerPlaintext` to `_appRouter.Resolve(appId)`.
     - Zeroize `innerPlaintext`.

3. **Outbound Egress Pipeline (`OutboundEgressPipeline.cs`)**:
   - **Pairwise 1:1 Messages (`context.RecipientIdentityId.HasValue`)**:
     - Advance sending chain: `session.StepSendingChain(_cryptoEngine)`.
     - Construct 41-byte header: `[0] = 0x02`, `[1..32] = DhPublicKey`, `[33..36] = MessageCounter`, `[37..40] = PreviousChainLength`.
     - Encrypt `innerPlaintext` with AES-GCM using the 41-byte header as Associated Data.
     - Save updated session to `_sessionRepo.SaveSessionAsync(...)`.
     - Frame: `[Header (41B)] + [Nonce (12B)] + [Ciphertext + Tag]`.
     - Enqueue outbox job with resolved route.
   - **Group Broadcast Messages (`context.RecipientIdentityId == null`)**:
     - Look up `GroupSenderKeyRatchet` via `_groupSenderRepo.GetSenderKeyRatchetAsync(context.ChannelId, context.SenderIdentityId, context.SenderDeviceId, ct)`.
     - Step ratchet: `ratchet.Advance(_cryptoEngine)` $\rightarrow$ derives `(Iteration, MessageKey)`.
     - Generate 12-byte random nonce.
     - Construct 21-byte header: `[0] = 0x03`, `[1..4] = KeyId`, `[5..8] = Iteration`, `[9..20] = Nonce`.
     - Encrypt `innerPlaintext` with AES-GCM using the 21-byte header as Associated Data.
     - Sign ciphertext: `ratchet.SignPayload(ciphertext, _cryptoEngine)` $\rightarrow$ Ed25519 signature.
     - Save updated ratchet to `_groupSenderRepo.SaveSenderKeyRatchetAsync(...)`.
     - Frame: `[Header (21B)] + [Ciphertext + Tag] + [Signature (64B)]`.
     - Enqueue outbox job with `DeliveryRouteType.RelayedGroup`.

---

### Milestone 2: Handshake Orchestration Service (`IHandshakeService`)

1. **Outbound Handshake Initiation (`InitiateHandshakeAsync`)**:
   - Input: remote peer `PreKeyBundle`, optional initial plaintext message payload.
   - Calls `X3dhAgreement.Initiate(localIdentityPriv, localIdentityPub, remoteBundle, _cryptoEngine)`.
   - Derives `X3dhInitiatorResult`.
   - Creates outbound session: `DirectRatchetSession.CreateFromX3dhInitiator(...)`.
   - If initial message payload is provided:
     - Steps sending chain: `session.StepSendingChain(_cryptoEngine)` $\rightarrow$ derives message key 0.
     - Encrypts initial payload with AES-GCM.
     - Assembles `HandshakeWireFrame` containing the handshake header + initial ciphertext.
   - Saves session to `IRatchetSessionRepository`.
   - Enqueues handshake outbox job to `IOutboxRepository`.

2. **Inbound Handshake Processing & Consent (`ReceiveInvitationAsync`)**:
   - Parses `HandshakeWireFrame`.
   - Inspects `PeerContact` state via `IPeerContactRepository`:
     - **If Blocked**: rejects silently and drops frame.
     - **If Untrusted / Not Found**: delegates to `IContactRequestCoordinator.HandleInboundRequestAsync(...)`, recording `ContactState.PendingApproval`. Defers cryptographic session derivation until user consent/approval.
     - **If Active (`Tofu` or `Verified`)**:
       - Looks up private signed prekey and atomically consumes OPK from `IPrivatePreKeyStore`.
       - Computes `X3dhAgreement.Receive(...)` to derive master shared secret.
       - Creates inbound session: `DirectRatchetSession.CreateFromX3dhResponder(...)`.
       - Saves session to `IRatchetSessionRepository`.
       - If initial message ciphertext is piggybacked:
         - Steps receiving chain: `session.StepReceivingChain(_cryptoEngine, targetCounter: 0)`.
         - Decrypts initial message with AES-GCM.
         - Dispatches decrypted payload to `_appRouter.Resolve(appId)`.
         - Zeroizes plaintext.

---

### Milestone 3: Sealed Ingress Envelope Adapter

1. **Relay Mailbox Ingress Adaptation (`ISealedEnvelopeUnwrapper`)**:
   - Relays deliver `MailboxEnvelope` instances keyed only by opaque `BlindedRoutingToken`, hiding `(SenderIdentityId, SenderDeviceId)` from the relay.
   - The sealed envelope adapter acts as an ingress adapter preceding `InboundIngressPipeline`:
     - Decrypts the outer sealed container using the recipient's identity / private delivery credentials.
     - Recovers `SenderIdentityId`, `SenderDeviceId`, `ChannelId`, and the inner `WirePayload`.
     - Constructs an `InboundWireEnvelope`.
     - Invokes `InboundIngressPipeline.ProcessInboundAsync(inboundEnvelope, ct)`.
   - Keeps `InboundIngressPipeline` completely decoupled from physical transport/relay mailbox details while achieving sealed sender anonymity and maintaining $O(1)$ session resolution.
