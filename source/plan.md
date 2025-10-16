### 16.3 Common InternalEnvelope processing via MediatR 

#### Progress Update (2025-10-12)
- **[COMPLETED] Centralize Chat routing (incl. admin ops) in `ProcessInternalEnvelopeHandler`**
  - `Percolator.Application/Network/ProcessInternalEnvelopeCommand.cs` now validates and dispatches:
    - `TextMessage`, `ReadReceipt`, `EmojiAnnotation`, `DeliveredReceipt` → `Post*` commands
    - `SignedAdminOperation` → `ApplySignedAdminOperationCommand`
    - `SignedAdminCommitOperation` → `ReceiveAdminCommitCommand`
    - `SignedKeyAdoptionConfirmation` → `ReceiveKeyAdoptionConfirmationCommand`
    - `KeyDistributionPayload` → `ReceiveKeyDistributionCommand`
- **[COMPLETED] Centralize DHT FindNode in `ProcessInternalEnvelopeHandler`**
  - Returns a response `InternalEnvelope` with `DhtEnvelope.FindNodeResponse` (contracts-mapped).
- **[COMPLETED] Simplify `DeliverOpaqueMessageHandler` to transport-only**
  - Focus: decrypt, infer session (fast/slow path), ratchet index upsert, resolve peer endpoint.
  - Chat and MessageQueue are delegated to `ProcessInternalEnvelopeHandler`.
  - DHT: handles only `PingRequest`; `FindNodeRequest` is no-op here.
  - Removed dead `HandleChatEnvelopeAsync` path.
- **[COMPLETED] Tests added for Chat routing in orchestrator**
  - New tests in `Percolator.ApplicationTests/Network/ProcessInternalEnvelopeHandlerTests.cs` cover chat admin variants and receipts/messages.

#### Remaining Work (detailed, handoff-ready)
- **[NEXT] Tighten allowed-case prefilters and document policy in-code**
  - In `Percolator.Application/Network/DeliverOpaqueMessageHandler.cs` ensure the allowed cases set exactly mirrors current behavior:
    - Allow: `ChatEnvelope`, `DhtEnvelope`, `PrekeyEnvelope`, `MessageQueueEnvelope`, `RelayOpaqueEnvelope`, response envelopes (`SubmitPreKeyBundleResponse`, `GetPreKeyBundleResponse`, `EnqueueOpaqueMessageResponse`, `FetchQueuedMessagesResponse`).
    - Log and ignore any other case.
  - Add a private static `HashSet<InternalEnvelope.ApplicationPayloadOneofCase>` to prevent drift and make tests assert against it.
- **[NEXT] Extend unit tests for orchestrator**
  - Add negative tests in `ProcessInternalEnvelopeHandlerTests`:
    - Missing/invalid GUID/PKH lengths for each chat/admin variant → `InvalidOperationException`.
    - Conflicting routing hints (both group and PKH) for message types that accept hints (should be rejected where applicable).
  - Add DHT FindNode mapping test with 0 results and multiple results.
- **[NEXT] MessageQueue response path audit**
  - Verify `ProcessInternalEnvelopeHandler` returns `InternalEnvelope` for:
    - `EnqueueOpaqueMessageRequest` → `EnqueueOpaqueMessageResponse`.
    - `FetchQueuedMessagesRequest` → `FetchQueuedMessagesResponse`.
  - Confirm `DeliverOpaqueMessageHandler` detects a non-null orchestrator response and encrypts early-response bytes (already coded), and that it logs Chat/MQ as no-op locally otherwise.
- **[NEXT] Handshake relay alignment**
  - Validate `ProcessRelayedOpaquePayloadCommand` flows are consistent with the central orchestrator model. Ensure relayed inner `InternalEnvelope` uses the same orchestrator routing and allowed-case constraints.
- **[NEXT] Documentation sync**
  - Update developer docs to reflect that application-layer orchestration occurs in `ProcessInternalEnvelopeHandler` and transport handler stays minimal.

- **Goal**: Centralize validation and dispatch for decrypted `InternalEnvelope` at the application layer (`ProcessInternalEnvelopeHandler`), while each caller pre-filters allowed envelope cases.

- **Request/Handler**
  - `Percolator.Application/Network/ProcessInternalEnvelopeCommand.cs`
  - Request fields:
    - `InternalEnvelope Envelope` (parsed from plaintext)
    - `SessionContext Context`: `SessionId?`, `SelfIdentityId`, `RemotePeerGuid?`
  - Handler: `ProcessInternalEnvelopeHandler`
    - Orchestrates application logic:
      - Chat (messages, receipts, annotations, admin ops, key rotation)
      - DHT `FindNodeRequest` → response envelope
      - MessageQueue requests → response envelopes
    - Throws on invalid/malformed fields; returns `null` when no response envelope is expected.

- **Call sites**
  - `Percolator.Application/Network/DeliverOpaqueMessageHandler.cs`
    - After decrypt + peer resolution, parse `InternalEnvelope` and pre-filter allowed cases.
    - Call `await _mediator.Send(new ProcessInternalEnvelopeCommand(envelope, ctx))`.
    - If orchestrator returns a response envelope (e.g., DHT FindNode or MQ), encrypt and return early bytes.
    - Otherwise, DHT `PingRequest`, Prekey submit, and Relay are handled locally in transport.
  - `Percolator.Application/Network/Handshake/HandleHandshakeResponderHelloCommand.cs`
    - After finalize + decrypt of the responder ratchet message, parse ResponderInnerHello directly from plaintext (no InternalEnvelope).
      Validate version and required fields, then complete handshake.
  - `Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs`
    - Parse inner `InternalEnvelope` from relay bytes
    - Pre-filter to the restricted set for relay (e.g., `HandshakeInitiatorHello` and any explicitly permitted app envelopes)
    - Optionally call the orchestrator for uniform logging/context

- **Why pre-filter at call sites**
  - Clear ownership: each handler explicitly defines what it accepts
  - No duplicate enums or indirection beyond the proto oneof case
  - The common handler remains a thin, auditable logging/context boundary

- **Testing**
  - Unit tests for call sites:
    - Allowed case(s) proceed
    - Disallowed cases are rejected before dispatch
  - Optional: verify `ProcessInternalEnvelopeHandler` logs source/case

- **Notes**
  - This adds a small, common logging/context step; downstream app-specific handlers (Chat, DHT, MQ, Prekey) remain unchanged
  - Maintains transport opacity: only Application-layer code handles `InternalEnvelope`

#### 16.3.1 Wiring steps (incremental, minimal risk)
- **DeliverOpaqueMessageHandler** (`Percolator.Application/Network/DeliverOpaqueMessageHandler.cs`)
  - After `InternalEnvelope.Parser.ParseFrom(plaintext.Value)`:
    - Pre-filter: allow only `ChatEnvelope`, `FileShareEnvelope`, `DhtEnvelope`, `PrekeyEnvelope`, `MessageQueueEnvelope`, `RelayOpaqueEnvelope`, and response envelopes used by this handler (e.g., `SubmitPreKeyBundleResponse`, `GetPreKeyBundleResponse`, `EnqueueOpaqueMessageResponse`, `FetchQueuedMessagesResponse`). If disallowed, log warning and return benign `DeliverOpaqueMessageResult()`.
    - Optional: `await _mediator.Send(new ProcessInternalEnvelopeCommand(internalEnvelope, new SessionContext(inferredSessionId.Value, _activeIdentityContext.Identity!.SelfIdentityId, directSession.RemotePeerId.Value)))`.
- **HandleHandshakeResponderHelloHandler** (`Percolator.Application/Network/Handshake/HandleHandshakeResponderHelloCommand.cs`)
  - After decrypting responder hello to a plaintext and parsing into `InternalEnvelope`:
    - Pre-filter: allow only `HandshakeResponderHello`. If not matched, throw.
    - Optional: send `ProcessInternalEnvelopeCommand` with `SessionContext` (resolved session id) for unified logging.
- **ProcessRelayedOpaquePayloadCommand** (`Percolator.Application/Network/Handshake/ProcessRelayedOpaquePayloadCommand.cs`)
  - After parsing inner envelope from relayed bytes:
    - Pre-filter: allow `HandshakeInitiatorHello` and any explicitly permitted relayed app envelopes (document decisions here as they evolve).
    - Optional: send `ProcessInternalEnvelopeCommand` with a `SessionContext` where `SessionId` may be null (pre-session), `SelfIdentityId` populated, and `RemotePeerGuid` if resolvable.

#### 16.3.2 Allowed cases per call site (initial policy)
- DeliverOpaqueMessageHandler: ChatEnvelope, FileShareEnvelope, DhtEnvelope, PrekeyEnvelope, MessageQueueEnvelope, RelayOpaqueEnvelope, plus response envelopes mentioned above.
-  Responder finalize is not an InternalEnvelope case; it is handled as a ratchet message carrying ResponderInnerHello.
-  ProcessRelayedOpaquePayloadCommand: Decrypts DR-only relayed payloads into an InternalEnvelope and processes normal application envelopes. It does not handle HandshakeInitiatorHello (which is not carried inside InternalEnvelope).

#### 16.3.2.a Opaque relay policy exception (authoritative)
- All `EnqueueOpaqueMessageCommand` payloads MUST be Double Ratchet ciphertext (DR bytes), making messages opaque to the Host.
- **Single exception**: `HandshakeInitiatorHello` is sent as a plaintext, standalone protobuf (not inside `InternalEnvelope`). This is required to bootstrap X3DH before a direct session exists.
- Implications:
  - Responder hello and all subsequent traffic MUST be sent as DR bytes whose plaintext is ResponderInnerHello (not an InternalEnvelope).
  - No other plaintext payloads are permitted via `EnqueueOpaqueMessageCommand`.


#### 16.3.4 TDD tasks (Red → Green → Refactor)
- Unit tests per handler asserting:
  - **Allowed case**: proceeds to existing switch/flow without throwing.
  - **Disallowed case**: rejected before dispatch (DeliverOpaque returns benign; ResponderHello throws; Relayed rejects).
- Unit test for `ProcessInternalEnvelopeHandler`: logs envelope case and returns unchanged envelope (smoke test).
- Refactor: ensure no duplication of allowed-case lists; define small private static `HashSet<InternalEnvelope.ApplicationPayloadOneofCase>` per handler for clarity.

#### 16.3.5 Risks and mitigations
- Risk: Divergence between documented allowed cases and implementation.
  - Mitigation: keep allowed sets centralized as constants within each file and covered by tests.
- Risk: Overly strict relayed pre-filter blocks future features.
  - Mitigation: start minimal (`HandshakeInitiatorHello` only); expand intentionally with tests.

# Percolator Group Chat E2E Plan (authoritative admin model)

### 16.1 Secure DeliverOpaqueMessage by removing session_id and introducing ratchet-key lookup (TDD)

Problem statement
- DeliverOpaqueMessageRequest currently carries `session_id`. This lets a peer assert which session the message belongs to, which is insecure and can be abused for session confusion/impersonation.
- We will remove `session_id` from `DeliverOpaqueMessageRequest` so it contains only `version` and `payload`.
- `DeliverOpaqueMessageHandler` must infer the session by reading the Double Ratchet header from the opaque payload using `new SessionRatchetMessage(request.PayloadBytes)`, then `GetHeader()` to extract the ratchet key (aka PreKey in the header). We need an efficient lookup from this ratchet header key to the local `DirectSession`.

Red–Green–Refactor strategy
- Red: write failing unit/integration tests that assume no `session_id` in the request and verify that the server can route/decrypt solely via ratchet header key lookup.
- Green: implement the minimal changes to pass tests (contracts, minimal lookup, handler change).
- Refactor: clean code, indexes, and naming without changing behavior; tests remain passing.

Scope of change
- Contracts: modify `Percolator.Contracts/Protos/messaging.proto` to remove `session_id` from `DeliverOpaqueMessageRequest`.
- Application: update `Percolator.Application/Network/DeliverOpaqueMessageHandler` to parse ratchet header and resolve the session via a new lookup service/repository.
- Infrastructure: ensure there is a fast index from ratchet header key → local session identifier (for the active identity) to avoid O(n) scans.

16.1.1 Investigation: can SkippedMessageKeyDbo support the lookup now?
- Goal: determine whether the existing `SkippedMessageKeyDbo` (and related tables) already persist mappings that contain the ratchet header key (or a derivable stable value) that can be used to identify a session quickly.
- Actions:
  - Review `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs` model for `SkippedMessageKeyDbo`.
  - Trace read/write paths where skipped keys are persisted (e.g., on out-of-order receive in Double Ratchet) to see if they include the ratchet header public key and the owning `DirectSessionId`.
  - Decide: if `SkippedMessageKeyDbo` contains (ratchet header key, direct_session_id) and is maintained for current chains, we can add an index and reuse it for lookup. If it’s only for message keys (KDF outputs) and not the header key, it is not suitable.

 Findings (from code)
 - `Percolator.Infrastructure/Persistence/SkippedMessageKeyDbo.cs` fields:
   - `SessionId : Guid`, `SelfIdentityId : int`, `RatchetKey : byte[]`, `MessageNumber : ulong`, `MessageKey : byte[]`.
 - `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs` mapping:
   - Table `SkippedMessageKeys` with unique index on `(SelfIdentityId, SessionId, RatchetKey, MessageNumber)`.
   - FK to `DoubleRatchetSessionDbo` on `(SessionId, SelfIdentityId)` with cascade delete.
 - Interpretation:
   - The presence of `RatchetKey` suggests storage of the remote header ratchet public key alongside per-message state, but rows are created only for "skipped" messages (out-of-order handling), not necessarily for every inbound message/ratchet step.
   - The unique index includes `MessageNumber`, meaning multiple entries can share the same `(SelfIdentityId, SessionId, RatchetKey)` across message numbers. There is no dedicated index on `(SelfIdentityId, RatchetKey)` for fast lookup independent of `SessionId`.

 Conclusions
- `SkippedMessageKeyDbo` is insufficient as the primary ratchet-key → session lookup because:
  - Coverage is opportunistic (only when messages are skipped). On the first inbound message for a session, there may be no row.
  - The current index shape is not optimized for lookups by `(SelfIdentityId, RatchetKey)`; it includes `SessionId` and `MessageNumber`.
- It could serve as an optional, opportunistic fallback if we also add a supporting index on `(SelfIdentityId, RatchetKey)`, but it cannot replace a deterministic mapping.

Recommendations
- Proceed with the dedicated `RatchetKeyIndex` in Step 1.2 for deterministic, fast lookups.
- Optional optimization: add a non-unique index on `SkippedMessageKeys (SelfIdentityId, RatchetKey)` to allow opportunistic hits; if multiple rows match, choose the most recent by `MessageNumber`/join to session, but keep `RatchetKeyIndex` as the authoritative source.
- Ensure `RatchetKeyIndex` is updated on:
  - Session establishment (seed initial remote header key when available).
  - Each successful inbound decrypt when the remote header ratchet public key advances.

 Decision
- We will NOT implement 1.1 as the primary approach. Proceed directly to 1.2 (RatchetKeyIndex) for deterministic lookups.

16.1.2 Design a dedicated ratchet-key index (if needed)
- If `SkippedMessageKeyDbo` is not a fit, define a new table (or repository) that maintains a minimal mapping for fast lookup:
  - Table name: `RatchetKeyIndex`
  - Columns:
    - `id` (PK)
    - `self_identity_id` (FK to local identity context)
    - `direct_session_id` (FK to DirectSession)
    - `ratchet_public_key` (BLOB, SPKI bytes of sender’s current ratchet header public key)
    - `updated_at_utc`
  - Unique index on (`self_identity_id`, `ratchet_public_key`).
  - Updated when:
    - A session is established (seed initial remote_sending key from responder/initiator data if known).
    - On every inbound decrypt that advances the remote chain (header ratchet key changes), upsert the latest mapping.
  - Cleanup: when a session is deleted, cascade delete its mappings.

 Implementation sub-steps (TDD)
- Red:
  - Add unit tests for `IRatchetKeySessionLookup.TryResolveAsync` and for `DeliverOpaqueMessageHandler` using header-based lookup.
- Green:
  - Define `IRatchetKeySessionLookup` (Application) and default implementation (Infrastructure) backed by `RatchetKeyIndex`.
  - Add EF entity `RatchetKeyIndexDbo` + configuration with unique index (`SelfIdentityId`, `RatchetPublicKey`).
  - Add repository `RatchetKeyIndexRepository` with `TryResolveAsync` and `UpsertAsync`.
  - Wire `DeliverOpaqueMessageHandler` to use `IRatchetKeySessionLookup` and THROW on lookup miss (per 1.3).
- Refactor:
  - Centralize upsert of `RatchetKeyIndex` at the point where remote header ratchet key advances.
  - Add minimal logging and counters (hits/misses, malformed headers).

16.1.3 Application changes (minimal to go green)
- `DeliverOpaqueMessageHandler.Handle`:
  - Construct `SessionRatchetMessage` from the request payload.
  - Call `GetHeader()` to obtain the ratchet header key (PreKey) bytes.
  - Use a new `IRatchetKeySessionLookup` to resolve (`self_identity_id`, `ratchet_public_key`) → `DirectSessionId`.
  - If found, continue with `_sessionManager.ReceiveMessageAsync(sessionId, sessionRatchetMessage)` as today; otherwise THROW (lookup miss is an error).
- `IRatchetKeySessionLookup` default implementation (infra):
  - If using `SkippedMessageKeyDbo`: add an index and implement the query.
  - Else: implement backed by `RatchetKeyIndex` table.

16.1.4 Two-step session inference (fast/slow path) for ratchet key misses
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
  
Implementation status
- Implemented in `Percolator.Application/Sessions/DirectSessionManager.TryInferAndReceiveAsync`.
- Handler wired to use slow path on fast-path miss in `Percolator.Application/Network/DeliverOpaqueMessageHandler.Handle`.
- Central upsert after successful slow-path decrypt keeps the index fresh.
- Design constraints:
  - Never expose or rely on peer-supplied session identifiers; inference remains local-only.
  - Keep the slow path isolated in the session manager/crypto layer to avoid leaking crypto details to the application layer.
  - Add minimal metrics: fast-path hits/misses, slow-path attempts and successes.
- Testing plan:
  - Unit tests to simulate a header with an unseen K_remote:
    - Fast-path miss triggers slow-path iteration; decryption with the correct session succeeds; index is updated.
  - Negative test: slow-path fails to authenticate for all sessions → handler throws (malformed/forged message).

16.1.5 Tests (Red first)
- Unit tests in `Percolator.ApplicationTests/Network/DeliverOpaqueMessageHandlerTests.cs`:
  - Build a minimal ratchet message with a known header key and arrange the lookup to return a specific session; assert handler decrypts and dispatches as before.
  - Assert that when lookup fails, the handler THROWS (lookup miss is an error).
- Integration test update (later): ensure messages flow end-to-end without `session_id` present; the server infers session via header key.

16.1.6 Migrations and performance
- Add DB migration for the `RatchetKeyIndex` (or index on the reused table).
- Ensure indexes are added for fast point lookups (B-tree on `ratchet_public_key`, filtered by `self_identity_id`).
- Consider size bounds and pruning policy (keep only the latest per (session, chain_role)).

16.1.7 Security and privacy notes
- Removing `session_id` prevents peers from steering processing toward an arbitrary session.
- The host continues to act as a dumb courier (opaque-only), and session inference occurs strictly via cryptographic headers.
- Ensure no logs persist sender identity for relayed messages; store only recipient queue state.

16.1.8 Refactor
- After green, refactor naming, extract small interfaces, and centralize ratchet-key updates on inbound decrypt to keep the index fresh.

Implementation status
- Centralized upsert on inbound decrypt implemented in `DirectSessionManager.ReceiveMessageAsync`.
- Retained handler-level upsert as a safety net (can be removed later if desired).

16.1.9 Proto change checklist (no code yet)
- Update `Percolator.Contracts/Protos/messaging.proto`:
  - Remove `session_id` from `DeliverOpaqueMessageRequest`.
  - Ensure `version` remains optional; payload stays `bytes payload`.
  - Regenerate and verify no unexpected downstream compile errors are left unresolved by the plan.

16.1.10 Application changes (detail design)
- `DeliverOpaqueMessageHandler`:
  - Replace `directSession` lookup by session id with header-driven lookup via `IRatchetKeySessionLookup`.
  - Decide failure semantics: return empty response (benign) vs. structured retry hint; tests will lock this in.
- Add `IRatchetKeySessionLookup` interface in Application with method:
  - `Task<DirectSessionId?> TryResolveAsync(PreKey ratchetPublicKey, int selfIdentityId, CancellationToken ct)`.

16.1.11 Infrastructure sketch (EF)
- If new table is needed, define EF entity `RatchetKeyIndexDbo` with fields listed in 16.1.2 and configuration:
  - Unique index: `HasIndex(x => new { x.SelfIdentityId, x.RatchetPublicKey }).IsUnique()`.
  - Foreign keys to `DirectSession` and self identity scope.
  - Repository `RatchetKeyIndexRepository` with `TryResolveAsync` and `UpsertAsync`.

16.1.12 TDD test matrix (Red first)
- Unit tests (`Percolator.ApplicationTests`):
  - Header resolves to known session → decrypt path hits existing dispatch (green criterion).
  - Header unknown → handler returns benign/no-throw (green criterion).
  - Malformed payload (cannot parse header) → benign/no-throw.
- Integration test (later):
  - End-to-end message delivery without `session_id` (after infra is in place).

16.1.13 Migration & back-compat choreography
- Introduce a temporary feature flag in tests to toggle the new request shape.
- Sequence:
  1) Add lookup infra + handler changes behind flag.
  2) Switch tests to the new request (Red → Green).
  3) Remove old `session_id` handling when all tests are green.

16.1.14 Observability
- Add debug-level logs for: header parsed, lookup hit/miss, session inferred, decrypt success/failure (without leaking sensitive material).
- Counters: lookup hits/misses, malformed headers.

Observability specifics
- Avoid logging sensitive cryptographic materials by default. Respect existing `CryptographyOptions.EnableCryptographicMaterialLogging` flag to gate detailed crypto logs for debug only.
- Suggested counters (names TBD):
  - `responder_hello_fastpath_miss`
  - `responder_hello_slowpath_attempts`
  - `responder_hello_decrypt_success`
  - `prehandshake_missing_or_expired`
  - `ratchet_index_upserts`

Implementation status (minimal)
- `DeliverOpaqueMessageHandler.Handle` logs:
  - Fast-path HIT at Debug with inferred `SessionId`.
  - Fast-path MISS at Warning, followed by slow-path attempt.
  - Slow-path SUCCESS at Information with inferred `SessionId`.
  - Null plaintext (skipped message) at Warning.
- Further metrics counters can be added later if needed.

1.16 Security validation
- Confirm no trust is placed on client-provided identifiers; only cryptographic headers drive session inference.
- Ensure logs do not persist sender identity for relayed messages; logs must be free of identifying content (see 16.1.1).

Implementation status (completed)
- Session inference inputs: `DeliverOpaqueMessageHandler` constructs `SessionRatchetMessage` and derives the ratchet header key from protobuf `RatchetHeader`; no client-supplied session identifiers are accepted.
  - Fast-path lookup uses `_ratchetLookup.TryResolveAsync(ratchetKey, selfIdentityId, ct)`. No external identifiers are trusted.
  - Slow-path trial decrypt is isolated to `DirectSessionManager.TryInferAndReceiveAsync` and iterates local sessions only.
- Logging hygiene:
  - `DeliverOpaqueMessageHandler.Handle` logs only lookup outcome and inferred `SessionId` value object; no sender identity or personally identifying information is logged.
  - Warning logged on null plaintext (skipped message) without payload details.
- Architectural boundaries:
  - Public transport remains opaque and minimal as per design; decrypted routing via internal envelope and MediatR is unchanged.
  - Contracts project continues to be protobuf-only.

## 16) Opaque Handshake App (status + design)

### 16.0 Overview and choices (finalized)
- Path 1 (peer-to-peer): Use public gRPC `TransportService.EstablishDirectSession` to bootstrap Alice↔Bob directly. This path remains supported and unchanged.
- Path 2 (host-relayed): Alice and Bob each have a DirectSession with the Host. Alice sends a handshake to Bob via the Host. 
  - Alice constructs an inner `InternalEnvelope { handshake_initiator_hello }` and sends it to the Host via queue/relay.
  - The Host delivers the inner opaque payload to Bob over the Bob↔Host session (e.g., via `RelayOpaqueEnvelope` or queued delivery).
  - On Bob’s node, after decrypting Bob↔Host, the inner payload is processed locally by `ProcessRelayedOpaquePayloadHandler` which parses `InternalEnvelope` and, when it contains `handshake_initiator_hello`, completes the responder-side handshake and establishes Alice↔Bob session.
  - the responder’s hello back to Alice is a ratchet message whose plaintext is ResponderInnerHello (no InternalEnvelope). It can be relayed asynchronously via the Host using the same mechanism.

#### 16.0.a Pre-handshake store and envelope choices (finalized)
- **Pre-handshake retention**: User-controlled retention and cleanup policy. No automatic purge in the store by default; retention is driven by user configuration or explicit purge commands.
- **Durability**: Sqlite-backed store will be the primary implementation for pre-handshake records. An in-memory implementation may exist for tests only.
- **Additional integrity**: No extra integrity/signature fields beyond AEAD provided by Double Ratchet. No additional MAC/signature inside the inner payload.
- **Interface sketch**: `IPreHandshakeSessionStore` in Application with methods: `SaveAsync`, `TryGetAsync`, `DeleteAsync`, `PurgeExpiredAsync`.
- **Schema sketch (Sqlite)**: Table `PreHandshakeSessions` with columns `{ id INTEGER PK AUTOINCREMENT, self_identity_id INT, remote_peer_id GUID NULL, recipient_public_key_hash BLOB NULL, initiator_ephemeral_private_key BLOB, signed_pre_key_id GUID NULL, one_time_pre_key_id GUID NULL, created_at_utc TEXT, expires_at_utc TEXT NULL }`. Index on `(self_identity_id)` and `(self_identity_id, expires_at_utc)`.

Implementation details (Sqlite and configuration)
- DDL (Sqlite example):
  ```sql
  CREATE TABLE IF NOT EXISTS PreHandshakeSessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    self_identity_id INTEGER NOT NULL,
    remote_peer_id TEXT NULL,
    recipient_public_key_hash BLOB NULL,
    initiator_ephemeral_private_key BLOB NOT NULL,
    signed_pre_key_id TEXT NULL,
    one_time_pre_key_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NULL
  );
  CREATE INDEX IF NOT EXISTS IX_PreHandshakeSessions_Self
    ON PreHandshakeSessions (self_identity_id);
  CREATE INDEX IF NOT EXISTS IX_PreHandshakeSessions_Self_Expires
    ON PreHandshakeSessions (self_identity_id, expires_at_utc);
  CREATE INDEX IF NOT EXISTS IX_PreHandshakeSessions_Self_RecipientPkh
    ON PreHandshakeSessions (self_identity_id, recipient_public_key_hash);
  ```
- Retention configuration (user-controlled; no auto-purge)
  - Application-level policy: when saving a new Pending entry for the same target, either replace existing Pending(s) for that peer (if `remote_peer_id` known) or coalesce by `recipient_public_key_hash`. A small configurable cap (e.g., MaxPendingPerPeer) may be enforced instead of replace.


Status update (current code):
- `HandshakeInitiatorHello`: Implemented. No signatures, canonical deterministic serialization, or timestamps are required.
- `HandshakeResponderHello`: Implemented. No signatures, canonical deterministic serialization, or timestamps are required.
- Remaining TODOs (both already noted in proto comments):
  - Initiator: add an encrypted bytes payload that can optionally contain the first opaque payload to the recipient.
  - Responder: add an encrypted bytes payload that must contain an opaque payload for the initiator with the direct session id (the session id must only appear inside this encrypted payload).

Notes:
- The Host remains oblivious to handshake semantics; it only transports opaque blobs. Parsing and session creation happen exclusively on the recipient node.
- The message queue’s `message_blob` continues to be treated as an opaque byte sequence carried inside the Host↔Client encrypted channel. For pre-session handshake, the inner payload is not DR-encrypted between Alice and Bob; security derives from the Host↔Client sessions and X3DH during bootstrap.

Known temporary excludes / follow-ups
- Group Chat E2E test `Phase3_GenesisGroupCreation_And_BaselineMessaging_Skeleton` is temporarily marked `[Ignore]` with a TODO. Reason: the `Percolator.Chat.Conversation` domain requires at least two participants on creation, but `ChatConversationResolver` currently creates an empty conversation on the first admin operation, triggering the invariant. Follow-up PR: seed participants (acting admin + members from op) on initial creation, or adjust resolver semantics to honor the invariant.

### 16.2 New feature (design): Opaque Handshake App envelope and handlers
- Goal: Support X3DH/Direct session negotiation over internal opaque messages between clients (via Host relay), not just via public gRPC calls. This decouples session establishment from the public transport interface and enables Bob→Alice handshake via MQ.

- Contracts (Protobuf; design-only — implement later):
  - Extend `Percolator.Contracts/Protos/internal_messaging.proto` with a new `oneof handshake_envelope` on `InternalEnvelope`:
    - `HandshakeInitiatorHello` (initiator → responder):
      - `bytes identity_key_spki`
      - `bytes signed_pre_key_spki`
      - `bytes payload_signature`
      - `bytes one_time_pre_key_spki` (optional)
      - `google.protobuf.Timestamp sent_timestamp_utc`
  - Backward compatibility: add version fields where necessary; keep envelope self-describing.

  16.2.a Encrypted inner payload schemas (Protobuf; all fields optional)
  - Initiator inner payload:
    - The inner message is always an `InnerEnvelope` serialized as bytes. 
  - Responder inner payload (carried inside responder hello; used by initiator to finalize):
    ```proto
    message ResponderInnerHello {
      optional uint32 version = 1;
      optional string direct_session_id = 2;
    }
    ```
  - Notes:
    - All fields are optional for forward/backward compatibility. The Application layer enforces required presence.
    - Version will be used to evolve these messages without breaking older nodes.

- Application handlers (design):
  - `HandleHandshakeInitiatorHello` (responder side):
    - Look up or create peer by SPKI (via `IPeerPublicSigningKeyStore` and `IPeerRepository`).
    - Use `IX3DHOrchestrator` to compute initial secrets and provision a `DirectSessionId` + initial double ratchet state.
    - Persist mapping in `IDirectSessionRepository` and session state via `IDoubleRatchetSessionStore`.
    - Emit `HandshakeResponderHello` back to initiator via `IMessageTransportService` using the new session.
  - `HandleHandshakeResponderHello` (initiator side):
    - Complete initiator-side state with `IX3DHOrchestrator`/`IDirectSessionManager`.
    - Persist repositories and stores as above.
    - Surface success to the caller (e.g., a Mediator command response or notification).

- Transport and routing:
  - The initiator hello is a standalone protobuf; the responder message is a DR ratchet message whose plaintext is ResponderInnerHello. After decryption of normal application traffic, InternalEnvelope is dispatched locally.
  - For host-relay, `ProcessRelayedOpaquePayloadHandler` parses the inner `InternalEnvelope` bytes and invokes responder-handshake logic when it finds `handshake_initiator_hello`.
  - No new public gRPC endpoints needed; reuse existing.

- Security model (current scope):
  - Keys exchanged are SPKI-encoded.
  - No signatures, no canonicalization, timestamps, or nonce-based replay protection in this phase.
  - Trust boundary: peers are discovered by public key; names are advisory labels stored in Identity.
  - Logs: do not persist raw keys or PII; only note handshake outcomes.

#### 16.2.b Error semantics
- Validation/signature failures: silently drop (no response) to avoid oracle or amplification vectors.
- Throttling/backpressure: respond with a minimal `NotBefore` handshake envelope carrying `retry_after_utc`; this envelope is also signed canonically.
- Responder hello session resolution (initiator side): two-step lookup
  1) Fast path: `IRatchetKeySessionLookup.TryResolveAsync(header.PreKey, selfId)`; expected to fail on the first responder message.
  2) Slow path: iterate all `Pending` pre-handshake sessions for `selfId` (from `IPreHandshakeSessionStore`) and attempt decrypt with each candidate’s ephemeral/X3DH context until one authenticates.
     - On success: finalize with that pre-handshake state and bind to the `direct_session_id` from decrypted payload; upsert ratchet-key index.
     - On failure (no candidate authenticates): treat as decrypt failure.
- Decrypt failure: throw (no message assertions in unit tests; just type and failure path).
- Missing/expired pre-handshake: throw (no message assertions in unit tests).

#### 16.2.c Rate limiting & bounds
- Configurable quotas with generous defaults (Application layer options):
  - Max handshake attempts per remote peer per minute (default: 60).
  - Max handshake attempts per node per minute (default: 600).
  - Max field sizes (SPKI and payloads) enforced before crypto (default: SPKI ≤ 1.5 KB, payload ≤ 8 KB).

#### 16.2.d Concurrency rule
- If both sides initiate simultaneously (A→B and B→A), the earliest `sent_timestamp_utc` wins.
- The “losing” side, upon detecting an established session for the pair, treats its in-flight handshake as no-op.

Idempotency rules and state transitions
- Pre-handshake record state machine (per self identity and per Pending record):
  - `Pending` → created by initiator when sending hello.
  - `Finalizing` → responder hello selected via slow-path; decrypt authenticated; binding to `direct_session_id` in progress.
  - `Completed` → session persisted and ratchet index upserted; pre-handshake record deleted.
  - `Expired` → retention policy or explicit purge; decrypt attempts MUST ignore expired entries.
- Duplicate responder hellos:
  - If `DirectSession` already exists for the peer pair, ignore (no-op).
  - If any pre-handshake candidate already transitioned to `Finalizing` or `Completed`, ignore duplicates.
  - If multiple `Pending` candidates exist, only the one that authenticates via decrypt proceeds; others remain `Pending` until expiry or manual cleanup.
- Simultaneous initiations (both sides initiate):
  - On observing a fully established `DirectSession` for the pair, the “losing” initiator deletes its `Pending` pre-handshake record without side-effects.

#### 16.2.f Implementation choices (current)
- DirectSessionManager will depend on `IPreHandshakeSessionStore` and own initiator-side intent persistence.
  - A new method will persist a `Pending` pre-handshake record (no session id yet). It stores:
    - `self_identity_id`, `recipient_public_key_hash`, `initiator_ephemeral_private_key`, `signed_pre_key_id`, optional `one_time_pre_key_id`, timestamps.
  - `ComposeAndEnqueueInitiatorHelloHandler` will call this method when sending the initiator hello.
- `HandleHandshakeResponderHelloHandler` keeps orchestration responsibilities:
  - Fast-path: resolve via `IRatchetKeySessionLookup.TryResolveAsync` and decrypt with `_sessions.ReceiveMessageAsync`.
  - Slow-path: iterate `IPreHandshakeSessionStore.EnumeratePendingAsync(selfId)`, attempt decrypt per candidate; on success, delete that `Pending`.
  - After decryption (both paths), delegate to `IDirectSessionManager` to finalize persistence/upsert (see below).
- `IDirectSessionManager.CompleteHandshakeAsync` (initiator-side finalize):
  - Decrypts responder hello when a persisted session may not exist (slow path).
  - Extracts the inner envelope and `SessionId` (via delegates provided by the handler).
  - Persists session state via `IDoubleRatchetSessionStore.SetSessionStateAsync`.
  - Upserts ratchet-key → session mapping via `IRatchetKeySessionLookup.UpsertAsync`.
  - Does not enumerate or delete `Pending` (the handler does that).

- Initiator first message encryption without session id:
  - Extend the no-session-id `EstablishSessionAsInitiatorAsync` to accept an optional `Plaintext`.
  - The session manager constructs a temporary initiator Double Ratchet session (in-memory) using the provided X3DH materials and encrypts the `Plaintext`, returning a `SessionRatchetMessage` to embed in `HandshakeInitiatorHello.EncryptedPayload`.
  - It still persists only the `Pending` pre-handshake record; no Double Ratchet state is stored yet.

- #### 16.2.e Responder response payload contents
- Outer (`HandshakeResponderHello` signed fields include this payload):
  - `version` (uint32)
  - `identity_key_spki` (bytes) — responder identity key SPKI
  - `response_payload` (bytes) — deterministically serialized `ResponderSessionDescriptor`
  - `sent_timestamp_utc` (Timestamp)
  - Optional `nonce` (bytes) if adopted

- Inner `ResponderSessionDescriptor` (Sessions-domain owned; deterministically serialized):
  - `version` (uint32)
  - `direct_session_id` (string or bytes) — responder-chosen identifier for the direct session
  - Optional `ratchet_seed_material` (bytes) — if the Sessions domain wishes to pass seed material
  - Optional `policy` submessage with fields like `max_skip` (uint32), `replay_window_seconds` (uint32)

- Notes:
  - The inner descriptor/payload is validated by the Sessions domain.
  - The initiator hands `response_payload` to `IDirectSessionManager`/`IX3DHOrchestrator` to finalize state; the Application layer does not parse ratchet internals.

- Storage interactions:
  - `IPeerPublicSigningKeyStore`: activate if changed (idempotent) upon seeing a new SPKI.
  - `IPeerRepository`: ensure peer records exist for both sides.
  - `IDirectSessionRepository`: upsert `PeerId` ↔ `DirectSessionId` mapping.
  - `IDoubleRatchetSessionStore`: create initial session state per X3DH outcome.

### 16.2.g Unit testing plan for DirectSessionManager handshake methods (Red → Green → Refactor)

- **Scope**: [DirectSessionManager.EstablishSessionAsInitiatorAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:149:4-204:5) (no session id; optional initial plaintext) and [DirectSessionManager.CompleteHandshakeAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:45:4-55:5) (finalize initiator via slow/fast path).

- **Happy-path scenario (Alice ↔ Bob)**
    1) **Setup prekeys (fixture)**: Bob publishes a valid prekey bundle (identity SPKI, signed prekey SPKI, optional one-time prekey SPKI). Generate proper EC keys via `ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)` and export with `ExportSubjectPublicKeyInfo`/`ExportECPrivateKey`.
    2) **Alice initiates**: Call [EstablishSessionAsInitiatorAsync(recipientPublicKeyHash, signedPreKeyId, oneTimePreKeyId, remoteIdentityKey, remotePreKey, sharedSecret, initiatorEphemeral, initialPlaintext, ct)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:149:4-204:5).
        - Assert: returns a `SessionRatchetMessage firstMessage`.
        - Assert: [IPreHandshakeSessionStore.SaveAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Network/Handshake/PreHandshakeSessionStore.cs:21:8-43:9) persisted a [PreHandshakeRecord](cci:2://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Network/Handshake/IPreHandshakeSessionStore.cs:27:4-45:6) including: `RemoteIdentityKeySpki`, `RemotePreKeySpki`, `X3DHSharedSecret`, and state snapshot fields (`CurrentRootKey?`, `CurrentDhRatchetPrivateKey?`, `SendingCounter`, `PreviousChainLength`, `RatchetFlag`).
    3) **Bob completes + decrypts first message**: On Bob’s node, use [EstablishSessionAsResponderAsync(...)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:206:4-252:5) to create the responder DR session, then [ReceiveMessageAsync(sessionId, firstMessage)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:255:4-317:5).
        - Assert: decrypted plaintext matches.
        - Assert: ratchet-key index upsert occurs on decrypt.
    4) **Responder hello back to Alice**: Build an inner `ResponderInnerHello { version, direct_session_id }`, encrypt it to Alice as `SessionRatchetMessage`.
    5) **Alice completes (slow-path)**: Call [CompleteHandshakeAsync(encryptedResponderHello, getEnvelope, getSessionId, ct)](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:45:4-55:5).
        - Assert: iterates [IPreHandshakeSessionStore.EnumeratePendingAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Infrastructure/Network/Handshake/PreHandshakeSessionStore.cs:45:8-76:9), reconstructs temporary DR session from the stored state, decrypts, extracts session id, upserts ratchet-key mapping, and returns `(sessionId, envelope)`.
        - Assert: Orchestrator deletes the matched pre-handshake record (handler-level responsibility).
    6) **Bi-directional messaging**:
        - Alice→Bob: [EncryptMessageAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:318:4-362:5) then Bob’s [ReceiveMessageAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:255:4-317:5) succeeds.
        - Bob→Alice: [EncryptMessageAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:318:4-362:5) then Alice’s [ReceiveMessageAsync](cci:1://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Sessions/DirectSessionManager.cs:255:4-317:5) succeeds.

- **Negative tests**
    - Missing/expired [PreHandshakeRecord](cci:2://file:///C:/Users/squir/source/repos/percolator/source/Percolator.Application/Network/Handshake/IPreHandshakeSessionStore.cs:27:4-45:6) → slow-path completion fails (no candidate authenticates).
    - Corrupted responder hello → decrypt fails; no ratchet index upsert.

- **Test locations**
    - `Percolator.ApplicationTests/Handshake/DirectSessionManagerHandshakeTests.cs` (new): DSM-focused tests with fakes for stores/lookups.
    - [Percolator.InfrastructureTests/Network/PreHandshakeSessionStoreTests.cs](cci:7://file:///C:/Users/squir/source/repos/percolator/source/Percolator.InfrastructureTests/Network/PreHandshakeSessionStoreTests.cs:0:0-0:0): keep persistence tests; add a round-trip assertion for required fields.

- **TDD steps**
    - Red: write failing tests assuming the above flows and required fields.
    - Green: minimal implementations in DSM and store to pass.
    - Refactor: extract common fixtures (key generation, shared secret, envelope builders).


- Testing plan (design; Step 16 = unit tests only):
  - Unit tests:
    - Happy path Initiator→Responder→ResponderHello→Initiator finalize.
    - Encrypted payload (responder) contains session id only inside ciphertext.
    - Encrypted payload (initiator) optional first opaque message path.
    - Concurrency rule: two-side initiation; single session established.
   - Minimal unit tests only; Step 17 integration test will cover the broader surface.
   - Unit tests assert behavior (success/failure paths) but SHOULD NOT assert exact exception messages.

- Rollout plan (single phase, no feature flags):
  - Implement opaque handshake end-to-end (initiator + responder) over internal messages in one phase.
  - Keep existing public gRPC for session establishment in place (for server bootstrap and interop). No deprecation in this plan.

- Migration considerations:
  - Schema-free at protobuf layer; repositories already support session upserts.
  - Ensure `SqlitePeerPublicSigningKeyStore` idempotency (already addressed) to avoid unique key collisions.
  - Public gRPC handshake RPCs DO exist today: see `Percolator.Contracts/Protos/messaging.proto` `TransportService.EstablishSession` and `TransportService.EstablishDirectSession`. The opaque handshake is additive; no removal in this phase.

- Observability:
  - Add debug/info logs for handshake start/success/failure without sensitive material.
  - Counters: initiator_hello_received, responder_hello_sent, finalize_success, finalize_failure.
      - Semantics: logically identical to the `EstablishDirectSessionResponse.Response` fields.

  Implementation notes (non-code):
  - Keep `Percolator.Contracts` protobuf-only (see `Percolator.Contracts/README.md` and repo rules). No C# services/handlers in Contracts.
  - Do NOT introduce a GUID-like message identifier. Follow a Signal-like approach: use sequence numbers and timestamps to enable ordering and dedup with minimal server-side metadata exposure.

### 16.3 Acceptance criteria (opaque handshake, single-phase)
- End-to-end opaque handshake flow succeeds between two nodes without invoking public gRPC handshakes.
- `IPeerPublicSigningKeyStore` activation is idempotent; no UNIQUE constraint violations with repeated handshakes.
- `IDirectSessionRepository` and `IDoubleRatchetSessionStore` contain exactly one session per pair after the handshake (no duplicates on replays).
- Delivering a follow-up `InternalEnvelope` over the new session works both directions (sanity check message round-trip).
- Logs contain only non-PII handshake lifecycle messages; no raw key material persisted.
- Existing public gRPC `EstablishSession`/`EstablishDirectSession` remain functional (no regressions) and are not used by the opaque tests.
- All unit tests for handshake pass in CI. Integration tests are deferred to Step 17.

- Application handlers (MediatR; design only):
  - `HandleHandshakeInitiatorHelloCommand` (receiver acts as Responder):
    - Input: initiator SPKI, signed pre-key, signature, optional one-time prekey.
    - Steps:
      1) Verify signature using `IX3DHManager.VerifySignature`.
      2) Build `X3dPreKeyBundle` and derive shared secret via `IX3DHOrchestrator` (responder path).
      3) Establish responder session via `IDirectSessionManager.EstablishSessionAsResponderAsync`.
      4) Upsert PKH→Peer mapping for the session’s remote peer (initiator) using `IPeerPublicSigningKeyStore`.
      5) Create responder payload (`ephemeral_key`, `session_id`), sign with local identity key via `IX3DHManager.SignPreKey`.
      6) Return `HandshakeResponderHello` envelope bytes.

  - `HandleHandshakeResponderHelloCommand` (receiver acts as Initiator):
    - Input: responder SPKI, response payload (ephemeral key + session id), responder signature.
    - Steps:
      1) Verify responder signature via `IX3DHManager.VerifySignature`.
      2) Complete initiator-side shared secret via `IX3DHOrchestrator.CompleteHandshake` using responder ephemeral key.
      3) Persist/Upsert `DirectSession` mapping and establish initiator session via `IDirectSessionManager.EstablishSessionAsInitiatorAsync`.
      4) Upsert PKH→Peer mapping for the session’s remote peer via `IPeerPublicSigningKeyStore`.
      5) No response envelope required (handshake complete).

  Code touchpoints:
  - New commands/handlers in `Percolator.Application/Prekey/` or `Percolator.Application/Network/` (choose folder by precedent: session network flow currently resides under `Percolator.Application/Network` and session services under `Percolator.Application/Sessions`).
  - DI: register the two new handlers via MediatR automatically (existing pattern). Ensure any new services are added in `Percolator.Application/Network/ServiceCollectionExtensions.cs` if needed.

- DeliverOpaqueMessage wiring (design only):
  - Handshake remains opaque to the server (host). The host never interprets handshake content; it only transports opaque blobs.
  - The parsing/dispatch of `HandshakeInitiatorHello` inside a relayed payload happens in `ProcessRelayedOpaquePayloadHandler` on the recipient node after decrypting host↔recipient.
  - `DeliverOpaqueMessageHandler` continues to handle direct `InternalEnvelope.HandshakeInitiatorHello` (peer-to-peer path).

  Code touchpoints:
  - File: `Percolator.Application/Network/DeliverOpaqueMessageHandler.cs`
    - Add a new case in `switch (internalEnvelope.ApplicationPayloadCase)` for `HandshakeEnvelope`.
    - Ensure we continue to update `PeerConnection` last-seen (`UpdateLastSeen` + `SaveAsync`) before dispatching.
    - When dispatching handshake commands, propagate `CancellationToken` and pass necessary context (remote `PeerId` from `IDirectSessionRepository`, and `sender_pkh` when relayed – see 16.2 MQ integration).

- Message Queue integration (design only):
  - Bob enqueues an opaque handshake blob targeting Alice’s PKH via `MessageQueue.EnqueueOpaqueMessage`. The host stores without interpreting contents.
  - Host persists the blob and, if Alice is online, wraps it solely for transport using `InternalEnvelope { RelayOpaqueEnvelope }` and sends it via `DeliverOpaqueMessage`. The host does not parse the inner payload.
  - On Alice’s node, `DeliverOpaqueMessageHandler` decrypts, recognizes `RelayOpaqueEnvelope`, extracts the inner opaque payload and processes it locally (e.g., if it is a handshake payload, it invokes the handshake commands; if it is a DR message post-session, it routes accordingly). The server remains oblivious to the semantics.
  - On offline scenarios, host retains the opaque blob until the client fetches or comes online; deletion happens upon per-message ACK (see 16.1.1).

  Code touchpoints:
  - Commands already exist in MessageQueue: `Percolator.MessageQueue/Commands/EnqueueOpaqueMessageCommand.cs`, `FetchQueuedMessagesQuery.cs` with handlers.
  - Host relay logic to be orchestrated by an Application service (e.g., new relay service under `Percolator.Application/Network/`):
    - Step 1: call `IMediator.Send(new EnqueueOpaqueMessageCommand(...))` to persist.
    - Step 2: attempt `DeliverOpaqueMessage` to the destination; on failure, log and return success for enqueue.
  - Receiver flow: upon `DeliverOpaqueMessage` success, immediately send `FetchQueuedMessagesQuery` (via mediator) and process any undelivered messages; apply ordering/dedup logic using sequence numbers and timestamps (see below).
  - Deliver side must include `sender_pkh` in `RelayOpaqueEnvelope` for accurate routing/auth.
  - Recipient behavior: upon successfully receiving a `DeliverOpaqueMessage`, the peer immediately issues `FetchQueuedMessagesQuery` to pull any additional queued messages it may have missed while offline.
    - Since Percolator.MessageQueue currently lacks a “delete one item” API, fetching may return the just-delivered message again. The peer must perform idempotent processing and deduplication (e.g., track processed message IDs/hashes).
  - Alice processes the initiator hello and replies by enqueuing `InternalEnvelope { HandshakeResponderHello }` via `MessageQueue.EnqueueOpaqueMessage`, targeting Bob’s PKH (Bob’s public identity key hash).
  - Host then delivers the responder hello to Bob (again via `DeliverOpaqueMessage`) and Bob also follows with `FetchQueuedMessagesQuery` to synchronize any missed messages.

  Notes:
  - We should consider a future enhancement to support selective removal of a single, successfully delivered message from `Percolator.MessageQueue` to reduce duplication on fetch. For now, deduplication remains the client’s responsibility.
  - Ordering & dedup strategy (Signal-like):
    - For messages inside an established direct session (peer↔peer), leverage Double Ratchet transport metadata (message number and previous chain length) plus the local store to prevent replays/duplicates.
    - For pre-session handshake messages relayed via MQ (`HandshakeInitiatorHello`/`HandshakeResponderHello`), include a sender-provided `sent_timestamp_utc` and a small sender-local monotonic counter. Receivers maintain a per-sender sliding window keyed by `{sender_pkh, counter}` (and optionally tolerate small clock skew using the timestamp) to ignore duplicates and out-of-window replays.
    - The Host does not persist unique IDs per message, keeping metadata minimal; peers are responsible for dedup windows.

#### 16.4 Verify Message Queue conforms to Signal-like "dumb courier" protocol (design + tests)

Goal: Ensure `Percolator.MessageQueue` acts as a minimal mailbox for offline delivery, handling only opaque blobs, with immediate forward when online, and delete upon successful delivery.

Non-functional contract (Signal-like):
- End-to-end encrypted blobs only; queue stores opaque bytes, never plaintext or keys.
- No permanent storage; messages are deleted immediately after successful delivery.
- FIFO per destination device; preserve enqueue order on delivery.
- Per-device queues; if we support multi-device in future, queues are keyed by device not only identity (plan ahead in schema/abstractions).
- Best-effort immediate delivery if recipient is online; otherwise enqueue.

Implementation alignment in our stack:
- Host relay path (Application):
    1) Always enqueue first via `EnqueueOpaqueMessageCommand`.
    2) If recipient appears online, attempt `DeliverOpaqueMessage` immediately; log failures without throwing.
    3) On successful `DeliverOpaqueMessage`, the receiver immediately issues `FetchQueuedMessagesQuery` to drain any backlog.
    4) Delete-on-delivery via client ACK (Signal-like):
     - Store: If recipient is offline, the Host stores the opaque blob in the recipient’s temporary queue.
     - Forward: When the recipient comes online, the Host forwards queued blobs (preserving FIFO) and continues to attempt immediate delivery for new arrivals.
     - Acknowledge: After the client safely persists the delivered blobs locally, it sends a separate ACK message for each delivered item.
     - Delete: Upon receiving the ACK, the Host deletes the acknowledged item from the queue. If the connection drops before ACK, the item remains queued and will be attempted again next time.
- Ordering & dedup:
    - No GUID IDs; use sequence + timestamp for pre-session handshake envelopes.
    - Established sessions rely on Double Ratchet transport counters; MessageQueue should not re-order blobs.

Verification plan (tests):
- Enqueue-then-deliver online: recipient marked online → Host attempts immediate `DeliverOpaqueMessage` → receiver calls `FetchQueuedMessagesQuery` and sees 0 or no duplicates; logs confirm non-throwing path.
- Enqueue offline then deliver later: recipient offline → enqueue only → later recipient comes online → Host delivers and receiver fetches remaining; verify FIFO and that duplicates (if any) are ignored by seq/timestamp windows.
  - Client sends ACKs for each delivered blob; server deletes only on ACK. Drop-connection simulation before ACK must leave the item queued for retry.
- FIFO preservation: enqueue N blobs A,B,C for same recipient; fetch returns [A,B,C] in order.
- Per-device queues (future): if/when multiple devices exist, verify device-keyed queues.
- Delete-on-delivery semantics: verify per-message ACK leads to deletion on server. Add a follow-up story for batch ACK/watermark deletion optimization (optional), keeping per-message ACK as the source of truth for correctness.

### 16.5 Blocking questions and open items
  - Message identity:
  - Confirm the exact counter semantics we will use for pre-session handshake envelopes (bit width, wraparound handling, and window size), acknowledging we are not using GUID-like IDs.
- Relay envelope schema:
  - Should `RelayOpaqueEnvelope` include optional `enqueue_timestamp` for observability and dedup hints?
- Security/auth:
  - Do we require the Host to attach an HMAC or signature over `sender_pkh + opaque_payload` to mitigate tampering-in-relay, or is double signature (inner) sufficient given trust in Host?
- Failure semantics:
  - Should `DeliverOpaqueMessage` in relay mode return a structured status (e.g., Delivered, Deferred, PeerUnknown) instead of just logging?
- Queue cleanup:
  - Since selective delete is not available, do we expose a batch acknowledgement mechanism (e.g., delete up to watermark ID) in a future iteration?
- Ordering and QoS:
  - Are there ordering guarantees per destination PKH? If required, plan sequencing or per-peer queues.
- Maximum payload size:
  - What are size limits for `opaque_payload` routed via `RelayOpaqueEnvelope`? Do we require chunking or fallback to the dedicated file-transfer service for larger envelopes?
- Test harness:
  - For Phase 2, do we disable background discovery and any unrelated hosted services to keep the environment deterministic?
- Back-compat:
  - Any existing clients depending solely on public gRPC handshake must continue to work; plan feature flags to toggle opaque handshake usage in tests.
- Update `GroupChatEndToEndTests.Phase2_Prekeys_Dht_And_Sessions_Establish` to follow 16.1’s ordering and to exercise the new opaque handshake path:

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