## Current Plan (Identity DDD Refactor)

1) Model PeerIdentity aggregate in `Percolator.Identity/Model/PeerIdentity.cs`.
   - Fields: `PeerId` (immutable), optional `DisplayName`, key history, single active key, trust state, timestamps.
   - Methods: `SetDisplayName`, `AddKey`, `ActivateKey`, `RevokeKey`, `Verify`.
   - Invariants: at most one active, non-expired key; cannot activate expired/revoked.
  1b) Verification and trust (user-driven support).
   - Allow UI to mark a peer as verified based on out-of-band/manual checks (e.g., QR/SAS/voice) via `Verify(OutOfBand|Manual)`.
   - Bind verification to the active key fingerprint; key rotations require re-verification of the new fingerprint.
   - Persist a `VerificationRecord` (method, fingerprint, verifiedAt, verifiedBy, optional note, validity window).
   - Derive `TrustState` from the active key’s verification; provide `Distrust/Unverify` operations.
   - Emit domain events: `PeerVerified`, `PeerDistrusted` for projections/telemetry.
  1a) Additional Identity features to implement.
   - Key validity windows: support not-before and future expires-at; enforce single active key at any time and overlap rules.
   - Key rotation scheduling: allow staging next key with future activation; zero-downtime rotation.
   - Key lifecycle management: track Created/Active/Expired/Revoked with revoke reasons and audit trail.
   - Public Key Hash (PKH) convenience: store/derive fingerprint for active key; expose `ActiveKeyHash` and lookup by hash.
   - Name/alias management: validation rules and (optional) uniqueness if `GetByNameAsync` is intended to be unique.
   - Repository queries: add `FindByPublicKeyHashAsync` and, if needed, queries by `TrustState`.
2) Introduce value objects in `Percolator.Identity/Model/`.
   - `IdentityKey` (SPKI, hash/fingerprint, notBefore, expiresAt, revokedAt), `TrustState`, `DisplayName`.
3) Revise `Percolator.Identity/IPeerRepository.cs` to persist/reconstitute `PeerIdentity`.
   - API: `GetByIdAsync`, `GetByNameAsync`, `FindByPublicKeyHashAsync`, `SaveAsync` (with concurrency token).
4) Infrastructure mapping and migration.
   - Map DBOs <-> aggregate; No migration/backfill for existing peers;
   - Implement as adapter/read model sourced from `PeerIdentity` (hash/SPKI lookups), fed by domain events.
6) Refactor app handlers to depend on aggregate behavior, not DBOs.
   - `InitiateHandshakeViaHostHandler`, `HandleHandshakeInitiatorHelloHandler`, and related identity updates.
7) Tests: domain + application.
   - Domain tests for key rotation, single active key, revoke/verify invariants.
   - App tests for observable behavior with repository mocked by aggregate, not DBO shape.
8) Telemetry, docs, and developer guidance.
   - Structured events for key rotations/trust; update docs and examples to new repository API.


## 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); 
    2) Alice publishes prekeys to host.
    3) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    4) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    5) Bob publishes his prekey bundles to Host.
    6) Charlie↔Host connect; mutual naming by SPKI.
    7) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    8) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
      9a) Alice sends a message, assert Bob and Charlie receive it.
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.

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