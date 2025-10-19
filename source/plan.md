
Red–Green–Refactor strategy
- Red: write failing unit tests and focus on creating the best possible interfaces. The build must succeed, but we expect new tests to fail at this point.
- Green: implement the minimal changes to pass tests (contracts, minimal lookup, handler change).
- Refactor: clean up the code, check to make sure business logic is sound, anticipate edge cases, and naming without changing behavior; tests remain passing.

- Two-step session inference (fast/slow path) for ratchet key misses
  - Rationale: In rare cases (e.g., a DH ratchet step that advances the remote header key before our index updates), the fast indexed lookup by header key may miss. We must still be able to correctly identify the session without any peer-supplied identifiers.
  - Fast Path (common case):
    - Lookup (`SelfIdentityId`, `RatchetPublicKey`) → `DirectSessionId` using `RatchetKeyIndex`.
    - Expected to succeed for ~99% of messages.
  - Slow Path (new key case): if fast path misses:
    - Iterate over all active sessions for the current `SelfIdentityId`.
    - For each session S:
      - Take the incoming header’s ratchet public key K_remote.
      - Perform trial DH using S’s current local DH ratchet private key.
      - Derive a trial message key using the ratchet KDFs.
      - Attempt to decrypt/authenticate the ciphertext.
      - If authentication succeeds, we’ve identified the correct session S. Update S’s state to the new remote header key, finalize decryption, and proceed.
    - After success, upsert `RatchetKeyIndex(SelfIdentityId, K_remote) → S.DirectSessionId`.

- Confirm no trust is placed on client-provided identifiers; only cryptographic headers drive session inference.
- Ensure logs do not persist sender identity for relayed messages; logs must be free of identifying content (see 16.1.1).

Non-functional contract (Signal-like):
- End-to-end encrypted blobs only; queue stores opaque bytes, never plaintext or keys.
- No permanent storage; messages are deleted immediately after ack
- FIFO per destination device; preserve enqueue order on delivery.
- Best-effort immediate delivery if recipient is online; otherwise enqueue.

### 1 Handle relayed plaintext HandshakeInitiatorHello (Completed)
Implemented fallback in `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs` and in `DeliverOpaqueMessageHandler.cs` to parse plaintext `HandshakeInitiatorHello` when DR parsing/decryption fails and dispatch `HandleHandshakeInitiatorHelloCommand`.

### 2 Relayed message wrapper (Completed)
- Proto already contains `RelayOpaqueEnvelope` and `RelayOpaqueResponse` in `Percolator.Contracts/Protos/internal_messaging.proto`.
- DB `AckId` implemented in infra repo: `Percolator.Infrastructure/MessageQueue/SqliteMessageQueueRepository.cs` with `TryEnqueueAsync` (AckId), `FetchAsync`, `DeleteByAckIdAsync`.
- Orchestration implemented in `Percolator.Application/Network/RelayOrchestrator.cs`: builds `RelayOpaqueEnvelope` with `MessageAckId`, encrypts/sends, decrypts `RelayOpaqueResponse`, validates AckId, deletes by AckId.
- Host replies with encrypted `RelayOpaqueResponse` in `DeliverOpaqueMessageHandler.cs` when `RelayOpaqueEnvelope` has `message_ack_id`.

#### 2.1 Relay for offline peers (Completed)
- After any valid `InternalEnvelope` is processed in `DeliverOpaqueMessageHandler.cs`, a loop calls `_relayOrchestrator.RelayNextAsync(...)` until the queue is empty or a failure occurs, acting as the "peer online" drain.
- Immediate per-enqueue relay attempt is also triggered via `TryRelayNextForPeerCommand` from chat dispatch handlers.

### 3 Chat

#### 3.2 Message Queue (Completed)
- MQ dispatch paths implemented for `TextMessage`, `ReadReceipt`, `EmojiAnnotation`, `DeliveredReceipt`, `SignedAdminOperation`, `SignedAdminCommitOperation` in `Percolator.Application/Apps/Chat/Handlers/`.
- Chat post handlers publish events excluding self in `Percolator.Chat/App/Handlers/`.
- Queue-first + immediate relay is live; ack-driven delete handled by the relay wrapper (see section 2).

### 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    8) Ensure mutual naming is completed among all peers where needed (e.g., after successful handshakes).
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.