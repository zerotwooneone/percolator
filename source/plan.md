
## 1) Update code for EstablishDirectSessionResponse proto changes
- **Contracts change**: In `Percolator.Contracts/Protos/messaging.proto` we replaced:
  - `EstablishDirectSessionResponse.Response.response_payload` and `payload_signature` with `remote_ephemeral_key` and `ratchet_message`.
  - `EstablishDirectSessionResponse.ResponsePayload.ephemeral_key` with `InnerEnvelope inner_envelope`.

- **Server-side (Application) updates**:
  - **`Percolator.Application/Network/EstablishDirectSessionHandler.cs`**
    - Build `ResponsePayload` with `inner_envelope` and `session_id`.
    - Produce `ratchet_message` by encrypting `ResponsePayload` bytes via `IDirectSessionManager.EncryptMessageAsync(sessionId, new Plaintext(payloadBytes))` and assign to `Response.ratchet_message`.
    - Set `Response.remote_ephemeral_key` to the responder’s ephemeral public key bytes.
    - Remove legacy signing of `response_payload` and any verification tied to it.
  - **`Percolator.Application/Network/PercolatorMessageService.cs`**
    - Map the new fields in `EstablishDirectSessionResponse.Types.Response` (no `ResponsePayload`/`PayloadSignature`; use `RemoteEphemeralKey` and `RatchetMessage`).

- **Client/responder-side updates**:
  - **`Percolator.Application/Sessions/ConversationService.cs`**
    - Stop verifying `payload_signature` and parsing `ResponsePayload` directly from `Response.response_payload`.
    - Use `Response.remote_ephemeral_key` for handshake completion inputs.
    - Decrypt `Response.ratchet_message` using pre-handshake state to obtain `ResponsePayload` bytes, then parse `ResponsePayload` and read `session_id` and optional `inner_envelope`.
    - Proceed to `EstablishSessionAsResponderAsync` with the derived keys.

- **Tests**:
  - Update unit/integration tests that:
    - Construct or assert on the old `response_payload/payload_signature` fields.
    - Expect `ResponsePayload.ephemeral_key`. Switch to asserting `Response.remote_ephemeral_key`, and decrypting `Response.ratchet_message` to validate the embedded `ResponsePayload`.

- **Follow-up validation**:
  - Regenerate protobufs and ensure all references compile.
  - Run `Percolator.ApplicationTests` and `Percolator.ApplicationIntegrationTests` to identify remaining callsites to update.

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



### 17 Correct Phase 2 test order (fix Phase2_Prekeys_Dht_And_Sessions_Establish)
    1) Alice↔Host connect; mutual naming by SPKI; Alice probes DHT (0 nodes); Alice publishes prekeys.
    2) Bob↔Host connect; mutual naming by SPKI; Bob probes DHT and discovers Alice’s PKH.
    3) Bob enqueues opaque handshake initiator envelope to Alice’s PKH; assert Alice responds and Bob completes the session establishment over opaque messages.
    4) Bob publishes his prekey bundle to Host.
    5) Charlie↔Host connect; mutual naming by SPKI.
    6) Charlie probes DHT and discovers both Alice and Bob (verify both PKHs are visible).
    7) Charlie initiates opaque handshakes to Alice and Bob via MQ (Host relays with `RelayOpaqueEnvelope`), both complete; verify `DirectSession` rows exist Charlie↔Alice and Charlie↔Bob.
    9) Alice creates a new group chat with members Bob and Charlie; assert repository state (conversation created, participants = {Alice, Bob, Charlie}).
      9a) Alice sends a message, assert Bob and Charlie receive it.
    10) Alice grants Bob admin rights for that group; assert admin set contains Alice and Bob.
    11) Bob removes Charlie from the group; assert participants = {Alice, Bob} and Charlie no longer has access to future group messages.
    12) Bob sends a group message; assert only Alice receives/sees it (Charlie must NOT receive it).
    13) Bob re-adds Charlie to the group; assert participants = {Alice, Bob, Charlie} and key distribution state updated.
    14) Alice sends a group message; assert both Bob and Charlie receive it.
