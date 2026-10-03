# Percolator.Application2 Implementation Plan

## 1. Overview & Architectural Role

`Percolator.Application2` serves as the central coordination and use-case orchestration layer in the Percolator microkernel architecture. It coordinates the execution of application workflows, governs data ingress and egress pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees.

Following Clean Architecture principles:
- **Dependencies Flow Inward**: `Application2` references `Percolator.Domain` and `Percolator.PluginSdk`. It has zero dependencies on concrete infrastructure implementations (no gRPC, SQLite, EF Core, or socket APIs).
- **Application Payload Opacity**: Decrypts and authenticates transport envelopes, then dispatches opaque inner byte buffers to registered application handlers (`Chat`, `Discovery`, `FileTransfer`) via constant-time `AppId` lookup.
- **Always Outbox First**: All outbound envelopes are persisted atomically to `IOutboxRepository` before transmission is attempted over network streams.
- **Cryptographic Logging Guardrails**: Respects `CryptographyOptions.EnableCryptographicMaterialLogging = false` by default. Diagnostic traces and loggers must never record root keys, chain keys, message keys, ephemeral private keys, or decrypted plaintexts.

---

## 2. Ingress & Egress Wire Framing

To support direct P2P, relayed 1:1, and relayed group messaging without ambiguity, wire payloads are framed with a 1-byte discriminant:

- **`0x01` (`WireFrameType.HandshakeInvitation`)**:
  - `[0]`: Frame type discriminant (`0x01`)
  - `[1..32]`: `InitiatorIdentityKey` (32 bytes)
  - `[33..64]`: `InitiatorEphemeralPublicKey` (32 bytes)
  - `[65..68]`: `SignedPreKeyId` (4 bytes, Big-Endian uint)
  - `[69..72]`: `OneTimePreKeyId` (4 bytes, Big-Endian uint, 0 if unused)
  - Parsed by `HandshakeWireFrame.TryParse`.
- **`0x02` (`WireFrameType.RatchetMessage`)**:
  - `[0]`: Frame type discriminant (`0x02`)
  - `[1..32]`: `DhPublicKey` (32 bytes)
  - `[33..36]`: `MessageCounter` (4 bytes, Big-Endian uint)
  - `[37..40]`: `PreviousChainLength` (4 bytes, Big-Endian uint)
  - `[41..52]`: Nonce (12 bytes)
  - `[53..^16]`: AES-256-GCM Ciphertext
  - `[^16..]`: AES-256-GCM Authentication Tag (16 bytes)
  - Parsed by `RatchetWireFrame.TryParse`.
- **`0x03` (`WireFrameType.GroupMessage`)**:
  - `[0]`: Frame type discriminant (`0x03`)
  - `[1..4]`: `KeyId` (4 bytes, Big-Endian uint)
  - `[5..8]`: `Iteration` (4 bytes, Big-Endian uint)
  - `[9..20]`: Nonce (12 bytes)
  - `[21..^(16+64)]`: AES-256-GCM Ciphertext
  - `[^(16+64)..^64]`: AES-256-GCM Authentication Tag (16 bytes)
  - `[^64..]`: Ed25519 Author Signature (64 bytes)
  - Parsed by `GroupWireFrame.TryParse`.

---

## 3. Milestones & Target Adapters

### Milestone 1: Ingress Pipeline Fixes & Wire Frame Dispatch
- **Inbound DH Ratchet Advancement Fix**:
  - In `InboundIngressPipeline.ProcessInboundAsync`:
    - When frame is `RatchetMessage`, inspect `wireFrame.DhPublicKey`.
    - If `session.RemoteEphemeralPublicKey == null || session.RemoteEphemeralPublicKey != wireFrame.DhPublicKey`, execute `session.StepDhRatchet(wireFrame.DhPublicKey, _cryptoEngine)`.
    - Then call `session.StepReceivingChain(_cryptoEngine, wireFrame.MessageCounter)`.
- **Discriminant Dispatching**:
  - Inspect byte 0 of `InboundWireEnvelope.WirePayload`:
    - If `0x01`: Hand off to `IHandshakeService.ReceiveInvitationAsync`.
    - If `0x02`: Process pairwise Double Ratchet.
    - If `0x03`: Process group Sender Key decryption and verify author Ed25519 signature.

### Milestone 2: Handshake Orchestration Service (`IHandshakeService`)
- **Outbound Handshake Initiation**:
  - Fetches remote `PreKeyBundle`.
  - Executes `X3dhAgreement.Initiate(...)` to derive `X3dhInitiatorResult`.
  - Instantiates `DirectRatchetSession` via `DirectRatchetSession.CreateFromX3dhInitiator`.
  - Persists session to `IRatchetSessionRepository`.
  - Encapsulates `HandshakeWireFrame` and persists initial outbox job to `IOutboxRepository`.
- **Inbound Handshake Processing & Consent**:
  - Parses `HandshakeWireFrame`.
  - Checks `PeerContact` state via `IPeerContactRepository`:
    - If `PeerTrustLevel.Blocked`: rejects and drops.
    - If `PeerContactState.PendingApproval`: records inbound request for user consent.
    - If `PeerTrustLevel.Tofu` or `Verified`: consumes private keys from `IPrivatePreKeyStore`, computes `X3dhAgreement.Receive(...)`, instantiates `DirectRatchetSession.CreateFromX3dhResponder`, and saves session.

### Milestone 3: Sealed Ingress Envelope Support
- **Relay Mailbox Ingress**:
  - Unwraps outer sealed sender envelopes received from relays to extract `(SenderIdentityId, SenderDeviceId)` using recipient profile/identity keys before seeking the Double Ratchet session, ensuring the relay never learns sender identities while preserving $O(1)$ session lookups on the client.
