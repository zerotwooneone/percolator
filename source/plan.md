

## 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    8) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
      8a) Alice sends a message, assert Bob and Charlie receive it.
    9) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    10) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    11) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    12) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    13) Alice sends a group message; assert both Bob and Charlie receive it.

## Notes for AI

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
- Ensure logs do not persist sender identity for relayed messages; logs must be free of identifying content (see privacy/logging above). Prefer correlation IDs and outcome metrics over identifiers.

### Non-functional contract (Signal-like):
- End-to-end encrypted blobs only; queue stores opaque bytes, never plaintext or keys.
- No permanent storage; messages are deleted immediately after ack
- FIFO per destination device; preserve enqueue order on delivery.
- Best-effort immediate delivery if recipient is online; otherwise enqueue.
- Idempotency: consumers must treat duplicate MessageId as no-op; send path may be at-least-once.
- Backpressure: define max in flight per peer; when exceeded, prefer enqueue to host relay with explicit SendResult indicating backpressure.

### Reverse Signal Example
This project already implements the Signal protocol model - peer A aquires a prekey bundle for peer B. Peer A (known as the x3dh inviter) performs the x3dh handshake using the prekey bundle and sends keys to peer B (possibly relayed through an intermediary). Peer B (known as the x3dh responder) then completes the handshake with peer A's keys and responds with a ratchet message containing the shared session id. 

However, this project also already implements a reverse Signal model where peer A (known as the reverse-signal requestor) sends their prekey bundle to Peer B (possibly relayed through an intermediary) as a request. Peer B (known as the x3dh initiator, and the reverse-signal acceptor) then initiates the handshake with peer A's keys and responds to the request with a custom message containing the handshake keys and a ratchet message containing the shared session id. Alice (known as the x3dh responder) then completes the handshake with peer B's keys. 