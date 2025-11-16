# DDD Crypto Refactoring Plan

This document proposes a Domain-Driven Design (DDD) refactor of Percolator.Cryptography to model X3DH + Double Ratchet handshakes and sessions with an explicit human-in-the-loop decision for Reverse-Signal (inbound invitation) flows.

---

## Test-Driven Development (TDD) Guidelines

Objective: Produce high‑quality, maintainable code by following the Red‑Green‑Refactor cycle. Tests describe observable behavior (public API and side‑effects), not implementation details.

Principles:
- Test the "what", never the "how". Avoid coupling to private methods, internal flags, or execution flow.
- One behavior at a time. Keep tests small and focused.
- Use Arrange‑Act‑Assert. Name tests with intention‑revealing behavior descriptions.
- Mock only external boundaries (DB, network, clock, random). Prefer fakes over heavy mocks.
- Ensure determinism. Control time and randomness via ports (`IClock`, `IRandom`).

Workflow:
1) Red (write the failing test)
- Write exactly one new failing test for a specific behavior.
- Assert only observable outcomes (return values, state published via public API, or external side‑effects via ports).
- Keep the solution compiling; add minimal scaffolding only if required by the compiler.
- Run the build and fix compiler errors, then run the build again until the build passes. Do not assume the build passes just because you change the code.
- Next, run the tests and confirm they fail. Do not assume the tests fail just because you change the code. The tests are supposed to fail at this point.

2) Green (make it pass)
- Write the minimum production code needed for that test to pass.
- Do not add features not covered by the failing test.
- Run the build and fix compiler errors, then run the build again until the build passes. Do not assume the build passes just because you change the code.
- Next, run the tests and confirm they pass. Do not assume the tests pass just because you change the code. Failing tests mean that we either need to update the code under test, the test code, or both. 

3) Refactor (improve design)
- Improve readability and structure in both code and tests; remove duplication.
- Preserve behavior; tests must stay green. Strengthen names and boundaries.
- Run the build and fix compiler errors, then run the build again until the build passes. Do not assume the build passes just because you change the code.
- Next, run the tests and confirm they pass. Do not assume the tests pass just because you change the code. Failing tests mean that we either need to update the code under test, the test code, or both.

Gates for each step:
- Build succeeds. Only the targeted test fails at Red; all pass at Green/Refactor.
- No reliance on internal implementation details. External effects verified via ports.

Definition of Done for a behavior:
- Meaningful test name documents the behavior.
- Test asserts only public contract and externally visible side‑effects.
- Edge cases covered or explicitly deferred with a tracked follow‑up.
- No dead code introduced; naming and structure are clear post‑refactor.

Anti‑patterns to avoid:
- Over‑mocking (mocking domain internals instead of ports).
- Asserting on logs, private fields, or call counts that leak implementation.
- Adding multiple behaviors in one step; causing multiple new failing tests at once.

## Architecture constraints and principles

- **Domain isolation**
  - Cryptography remains independent of other domains. Cross-domain needs are expressed as ports (interfaces) and implemented in Application/Infrastructure.
- **Public API byte policy**
  - New public APIs must not expose raw `byte[]`. Use intention-revealing value objects (`Plaintext`, `Ciphertext`, `AssociatedData`, key types). Exception: existing `CryptoUtils` remains public and may use `byte[]` as previously agreed.
- **Session as the authority**
  - `SecureSession` owns ratchet state; callers cannot mutate state directly. All mutations occur via domain behaviors.
- **Explicit decisions and time**
  - `PendingSession` models human decision and expiration with `IClock` and policies.
- **Persistence is an implementation detail**
  - Repositories persist aggregates; EF/DB schemas follow the domain model, not the other way around.
 - **Canonical naming and adapters**
   - Use `PreKeyBundle` as the canonical VO; provide adapters to/from existing `X3dPreKeyBundle` during cut-over.
 - **No secret logging in domain**
   - Aggregates/services do not log cryptographic material. If diagnostics are required, they happen in Application adapters.

---

## Glossary

- **SessionRatchetMessage**
  - A ratchet-framed message containing a header and a ciphertext.
  - Header includes the sender's current ratchet public key, message counter, and previous chain length.
  - Used on the wire between peers and internally by `SecureSession` for ratchet advancement, skipped-key handling, and AD construction.
  - When to use: pass to/from `SecureSession` encrypt/decrypt APIs and to transport components. Application should not modify or interpret header fields.

- **Ciphertext**
  - Opaque encrypted bytes representing the payload only (no ratchet header).
  - Produced/consumed inside the domain when sealing/unsealing the payload portion of a `SessionRatchetMessage`.
  - When to use: within domain operations or as a VO for payloads in tests; avoid exposing as the on-the-wire format.

---

## Phase 1: Domain Analysis & Model Identification

- **Context and goals**
  - The current library exposes low-level cryptographic utilities (X3DH, Double Ratchet, signatures) directly to application code, forcing app layers to orchestrate session state and handshake decisions.
  - We will introduce rich domain aggregates that encapsulate session lifecycle and handshake control, including a pending approval state for inbound (Reverse-Signal) flows.

- **Key user journeys to support**
  - Standard Outbound Handshake (we fetch peer’s pre-key bundle, initiate session, optionally produce first message).
  - Reverse-Signal Inbound Handshake (we receive a peer’s bundle as an invitation, initiate locally, then either auto-respond or wait for human approval before sending our response).

- **Core domain additions (summary)**
  - Aggregates: `SecureSession`, `PendingSession`.
  - Value Objects: `RatchetState`, `ProtocolVersion`, `Plaintext`, `Ciphertext`, `AssociatedData`, `SessionId`, `PendingSessionId`, `PeerId`, policies (`ApprovalPolicy`, `ExpirationPolicy`, `SkippedKeyPolicy`).
  - Services (ports): `SessionCrypto`, `HandshakePlanner`.
  - Repositories: `ISessionRepository`, `IPendingSessionRepository`.

- **Core Aggregates**
  - **SecureSession (Aggregate Root)**
    - Identity: `SessionId`.
    - Purpose: Represents an established, active end-to-end encrypted session with one remote `PeerId`.
    - Owns: Double Ratchet state (root key, chain keys, counters, header state), protocol version, last-used timestamps.
    - Invariants:
      - Ratchet state progresses monotonically (no key/counter regressions).
      - Replay/ordering checks enforced per protocol guarantees.
      - Associated Data (AD) rules are consistent for encrypt/decrypt.
    - Behaviors:
      - `Encrypt(Plaintext, AssociatedData) -> Ciphertext`
      - `Decrypt(Ciphertext, AssociatedData) -> Plaintext`
      - `RotateIfNeeded()`, `TouchLastUsed()` (housekeeping/policy-driven updates)
    - Factory:
      - `EstablishFromX3DH(PreKeyBundle, IKeyStore, ICryptoPrimitives)`

  - **PendingSession (Aggregate Root)**
    - Identity: `PendingSessionId`.
    - Purpose: Represents a session initialized from a peer’s invitation (inbound bundle) that is awaiting a decision (auto-respond or manual approval), and can be rejected or expired.
    - State: `AwaitingApproval | Approved | Rejected | AutoResponded`.
    - Owns: `HandshakeInvitation` (value object), policy snapshot (approval/expiration), timestamps, protocol version.
    - Behaviors:
      - `ApproveAndRespond(ICryptoPrimitives, IKeyStore) -> HandshakeResponse`
      - `AutoRespond(ICryptoPrimitives, IKeyStore) -> HandshakeResponse` (policy-permitting)
      - `Reject()` (terminal)
      - `Expire()` (policy-driven terminal)
    - Domain events (examples): `PendingSessionApproved`, `PendingSessionRejected`, `PendingSessionExpired`.

- **Supporting Entities / Value Objects**
  - `PeerIdentity` (entity or cross-domain reference): `PeerId`, `IdentityPublicKey`, trust metadata.
  - `HandshakeInvitation` (VO): serialized inbound pre-key bundle with metadata and protocol version.
  - `PreKeyBundle` (VO): identity key, signed pre-key, one-time pre-key, signatures, protocol version.
  - `HandshakeResponse` (VO): our handshake response bundle or the first message to send.
  - IDs (VO): `SessionId`, `PendingSessionId`, `PeerId`.
  - Payload VOs: `Plaintext`, `Ciphertext`, `AssociatedData`.
  - Policies (VOs): `ApprovalPolicy` (e.g., auto-respond allowlist), `ExpirationPolicy` (e.g., TTL for pending).
  - Crypto state (VO): `RatchetState` (root/chain keys, counters, header params), `SessionKeys` (as needed).

- **Domain services (pure, stateless)**
  - `HandshakePlanner`: validates invitations/pre-key bundles (sig verification, key formats, protocol compatibility), and determines required inputs for session establishment.
  - `SessionCrypto`: interface grouping crypto operations used by aggregates (X3DH key agreement, Double Ratchet steps, sign/verify). Implementations adapt the existing utility code.
  - `InboundMessageResolver`: resolves an inbound `SessionRatchetMessage` to a `SessionId` via fast/slow path:
    - Fast path: `IRatchetKeyIndex.TryResolveAsync(header.PreKey)` -> `SessionId?`
    - Slow path: enumerate candidate sessions and attempt `SecureSession.Decrypt(...)` until one succeeds; on success, persist updated state and `IRatchetKeyIndex.UpsertAsync`.

- **Application services (orchestrators)**
  - `HandshakeService`:
    - `InitiateFromInvitation(HandshakeInvitation) -> PendingSessionId`
    - `ApproveSession(PendingSessionId) -> (HandshakeResponse, SessionId)`
    - `RejectSession(PendingSessionId)`
    - `InitiateStandardHandshake(PeerId, Plaintext? initialMessage = null) -> (SessionId, Ciphertext? initialCiphertext)`
  - `SecureMessagingService`: coordinates loading `SecureSession` from persistence and calling `Encrypt/Decrypt`, then saving mutated state.

- **Repositories (ports)**
  - `ISessionRepository` (SecureSession persistence)
  - `IPendingSessionRepository` (PendingSession persistence)
  - `IRatchetKeyIndex` (fast-path header public key -> `SessionId` mapping)
  - `ISessionCatalog` (enumeration port for slow-path; adapter scopes to current self-identity)
  - Optionally reuse Network domain identity storage for `PeerIdentity` (via a cross-domain port), rather than introducing a new repository here.

- **Integration boundaries (ports)**
  - `ICryptoPrimitives` (adaptor for existing crypto util functions)
  - `IKeyStore` (local identity keys, signed prekey rotation, one-time prekeys)
  - `IClock`, `IRandom` (testability)
  - `IOutbox` (to queue handshake responses/messages for transport)
  - `INotification` (to notify UI about pending approvals)

---

Next: Phase 2 – New Implementation Plan

---

## Phase 2: New Implementation Plan

- **Aggregate: SecureSession (AR)**
  - Properties
    - `SessionId Id`
    - `PeerId RemotePeerId`
    - `RatchetState State` (root key, send/recv chains, counters, skipped keys policy)
    - `int ProtocolVersion`
    - `DateTimeOffset CreatedAtUtc, LastUsedAtUtc`
  - Methods
    - `Ciphertext Encrypt(Plaintext pt, AssociatedData ad)`
    - `Plaintext Decrypt(Ciphertext ct, AssociatedData ad)`
    - Internal invariants enforcement: monotonic counters, header validation, skipped-key retrieval, `RotateIfNeeded()`, `TouchLastUsed()`
  - Factory
    - `static SecureSession EstablishFromX3DH(PreKeyBundle bundle, IKeyStore store, ICryptoPrimitives crypto)`

- **Aggregate: PendingSession (AR)**
  - Properties
    - `PendingSessionId Id`
    - `PeerId RemotePeerId`
    - `HandshakeInvitation Invitation`
    - `ApprovalState State` (AwaitingApproval, Approved, Rejected, AutoResponded)
    - `int ProtocolVersion`
    - `DateTimeOffset CreatedAtUtc, ExpiresAtUtc?`
    - Policy snapshot: `ApprovalPolicy`, `ExpirationPolicy`
  - Methods
    - `HandshakeResponse ApproveAndRespond(ICryptoPrimitives crypto, IKeyStore store)`
    - `HandshakeResponse AutoRespond(ICryptoPrimitives crypto, IKeyStore store)` (guarded by `ApprovalPolicy`)
    - `void Reject()`
    - `void Expire(IClock clock)`
  - Domain events
    - `PendingSessionApproved`, `PendingSessionRejected`, `PendingSessionExpired`
  - Factory
    - `static PendingSession FromInvitation(HandshakeInvitation inv, ApprovalPolicy policy, ExpirationPolicy exp, IClock clock)`

- **Value Objects (implement as records/structs)**
  - `HandshakeInvitation`, `PreKeyBundle`, `HandshakeResponse`, `Plaintext`, `Ciphertext`, `AssociatedData`, `SessionId`, `PendingSessionId`, `PeerId`, `ProtocolVersion`
  - Policies: `ApprovalPolicy` (allowlist, trust threshold), `ExpirationPolicy` (TTL), `SkippedKeyPolicy` (e.g., max skipped keys)
  - `RatchetState` (root/chain keys, counters, header params, skipped store limits governed by `SkippedKeyPolicy`)

- **Domain services (pure)**
  - `HandshakePlanner`
    - `ValidateInvitation(HandshakeInvitation inv, ICryptoPrimitives crypto) -> void`
    - `PlanEstablishment(PreKeyBundle bundle) -> EstablishmentPlan` (inputs required for X3DH)
  - `SessionCrypto` (ports)
    - `X3DH_Initiate(localIdPriv, PreKeyBundle bundle) -> (SharedSecret, EphemeralPublic)`
    - `DR_Encrypt(state, pt, ad) -> (ct, newState)`
    - `DR_Decrypt(state, ct, ad) -> (pt, newState)`
    - `VerifySignature(pub, msg, sig) -> bool`

- **Application services (orchestrators)**
  - `HandshakeService`
    - `Task<PendingSessionId> InitiateFromInvitation(HandshakeInvitation inv)`
      - `HandshakePlanner.ValidateInvitation(inv)`
      - Create `PendingSession.FromInvitation(...)`
      - `IPendingSessionRepository.AddAsync(pending)`
      - `INotification.NotifyPendingSessionCreated(pending.Id, pending.RemotePeerId)`
    - `Task<(HandshakeResponse Response, SessionId Session)> ApproveSession(PendingSessionId id)`
      - Load pending; `pending.ApproveAndRespond(crypto, keyStore)`
      - Establish `SecureSession` (either inside `ApproveAndRespond` or here via factory)
      - `ISessionRepository.AddAsync(session)`; `IPendingSessionRepository.DeleteAsync(id)`
      - `IOutbox.EnqueueHandshakeResponse(pending.RemotePeerId, response)`
    - `Task RejectSession(PendingSessionId id)`
      - Load; `pending.Reject()`; `IPendingSessionRepository.UpdateAsync(pending)`
    - `Task<(SessionId Session, Ciphertext? Initial)> InitiateStandardHandshake(PeerId peer, Plaintext? firstMessage = null)`
      - Fetch `PreKeyBundle` from server
      - `SecureSession.EstablishFromX3DH(...)`
      - If `firstMessage` present, `Encrypt` and return
  - `SecureMessagingService`
    - `Task<Ciphertext> Encrypt(SessionId id, Plaintext pt, AssociatedData ad)`
    - `Task<Plaintext> Decrypt(SessionId id, Ciphertext ct, AssociatedData ad)`

- **Repositories (interfaces)**
  - `ISessionRepository`
    - `Task AddAsync(SecureSession s)`
    - `Task<SecureSession?> GetAsync(SessionId id)`
    - `Task UpdateAsync(SecureSession s)`
  - `IPendingSessionRepository`
    - `Task AddAsync(PendingSession p)`
    - `Task<PendingSession?> GetAsync(PendingSessionId id)`
    - `Task UpdateAsync(PendingSession p)`
    - `Task DeleteAsync(PendingSessionId id)`
  - Notes
    - Repositories operate within a scoped SelfIdentity context provided by the Application layer; do not hardwire identity concerns into the Cryptography domain.

- **Ports**
  - `ICryptoPrimitives`, `IKeyStore`, `IRatchetKeyIndex`, `ISessionCatalog`, `IOutbox`, `INotification`, `IClock`, `IRandom`

- **Testing plan**
  - Unit tests for: PendingSession transitions (approve/auto/reject/expire), SecureSession invariants (encrypt/decrypt, counter monotonicity), HandshakePlanner validation.
  - Integration tests: Standard outbound establishment; inbound invitation with manual approval; inbound with auto-approval per policy.

---

Next: Phase 3 – Database Refactoring Plan

---

## Phase 3: Database Refactoring Plan

- **New tables**
  - Sessions
    - Stores SecureSession ratchet state and metadata
    - Example schema (SQLite):
      ```sqlite
      CREATE TABLE Sessions (
        SessionId TEXT NOT NULL PRIMARY KEY,            -- GUID as canonical string
        RemotePeerId TEXT NOT NULL,
        ProtocolVersion INTEGER NOT NULL,
        RootKey BLOB NOT NULL,
        SendChainKey BLOB NOT NULL,
        SendCounter INTEGER NOT NULL,
        RecvChainKey BLOB NOT NULL,
        RecvCounter INTEGER NOT NULL,
        AssociatedData BLOB NULL,
        CreatedAtUtc INTEGER NOT NULL,                  -- Unix epoch millis
        LastUsedAtUtc INTEGER NOT NULL
      );
      CREATE INDEX IX_Sessions_RemotePeerId ON Sessions(RemotePeerId);
      ```
  - PendingSessions
    - Stores PendingSession with serialized invitation and state
    - Example schema (SQLite):
      ```sqlite
      CREATE TABLE PendingSessions (
        PendingSessionId TEXT NOT NULL PRIMARY KEY,
        RemotePeerId TEXT NOT NULL,
        ProtocolVersion INTEGER NOT NULL,
        Invitation BLOB NOT NULL,
        State INTEGER NOT NULL, -- 0 AwaitingApproval, 1 Approved, 2 Rejected, 3 AutoResponded
        CreatedAtUtc INTEGER NOT NULL,                  -- Unix epoch millis
        ExpiresAtUtc INTEGER NULL
      );
      CREATE INDEX IX_PendingSessions_RemotePeerId ON PendingSessions(RemotePeerId);
      CREATE INDEX IX_PendingSessions_State ON PendingSessions(State);
      ```

- **Reuse vs new**
  - If `DirectSessions` already persists DR state for Crypto, consider evolving it to match the `Sessions` schema (rename/augment) instead of creating a parallel table.
  - Otherwise introduce `Sessions`/`PendingSessions` alongside existing tables.

- **Retirement plan (no data migration in scope)**
  - Introduce new repos/entities and migrate application code to them.
  - Mark legacy tables as deprecated; drop them in a later migration once references are removed.

---

Next: Phase 4 – Application-Layer Migration Plan

---

## Phase 4: Application-Layer Migration Plan

- **Identify call sites**
  - grep for current crypto utility usage across app services, gRPC handlers, controllers.
  - Categories: (1) standard outbound initiation, (2) reverse-signal inbound initiation, (3) message encrypt, (4) message decrypt.

- **Migration steps**
  1. Introduce domain contracts (aggregates, VOs, repositories, ports) with no implementation.
  2. Implement application services `HandshakeService`, `SecureMessagingService` behind interfaces.
  3. Cut-over: delete legacy crypto code and fix compiler errors by adopting the new domain and services at each call site.
  4. Convert callers incrementally; keep each commit compiling with tests green.

- **Before/After examples**
  - Standard outbound
    - Before:
      ```csharp
      var bundle = await _server.GetPreKeyBundleAsync(peerId);
      var (secret, ephPub) = CryptoUtils.X3DH(localIdPriv, bundle);
      var session = CryptoUtils.DoubleRatchet.Init(secret, ...);
      var firstMsg = CryptoUtils.DoubleRatchet.Encrypt(session, plaintext);
      // persist session manually, send firstMsg
      ```
    - After:
      ```csharp
      var (sessionId, initialCipher) = await _handshakeService.InitiateStandardHandshake(peerId, plaintext);
      await _transport.SendAsync(peerId, initialCipher);
      ```
  - Reverse-Signal inbound (manual approval)
    - Before:
      ```csharp
      var (secret, ephPub) = CryptoUtils.X3DH(localIdPriv, invitation.Bundle);
      var session = CryptoUtils.DoubleRatchet.Init(secret, ...);
      var response = CryptoUtils.MakeHandshakeResponse(ephPub, ...);
      await _transport.SendAsync(peerId, response);
      ```
    - After:
      ```csharp
      var pendingId = await _handshakeService.InitiateFromInvitation(invitation);
      // UI presents decision
      var (response, sessionId) = await _handshakeService.ApproveSession(pendingId);
      await _transport.SendAsync(invitation.PeerId, response);
      ```
  - Encrypt/Decrypt
    - Before:
      ```csharp
      var ciphertext = CryptoUtils.DoubleRatchet.Encrypt(session, plaintext);
      var plaintext = CryptoUtils.DoubleRatchet.Decrypt(session, ciphertext);
      ```
    - After:
      ```csharp
      var s = await _sessions.GetAsync(sessionId);
      var ct = s.Encrypt(new Plaintext(bytes), ad);
      await _sessions.UpdateAsync(s);

      s = await _sessions.GetAsync(sessionId);
      var pt = s.Decrypt(ct, ad);
      await _sessions.UpdateAsync(s);
      ```

- **Telemetry/UX**
  - Emit events when `PendingSession` is created/approved/rejected.
  - UI surface to list and act on pending sessions.

- **Testing**
  - Update unit/integration tests to target new services and aggregates.
  - Add end-to-end tests for standard and reverse-signal flows (manual/auto approval).

---

## Implementation roadmap (AI-sized steps with TDD)

- **Step 1: Define core Value Objects and IDs (no behavior)**
  - Red: Tests assert creation, equality, and validation of `SessionId`, `PendingSessionId`, `PeerId`, `Plaintext`, `Ciphertext`, `AssociatedData` (no `byte[]` exposure in public API).
  - Green: Implement minimal records/structs and guards.
  - Refactor: Remove duplication, ensure naming clarity and serdes helpers if needed.
  - Deliverable: New VO types in Percolator.Cryptography with unit tests.

- **Step 2: RatchetState VO shape (persistence-ready, immutable operations later)**
  - Red: Tests for shape, invariants scaffolding (counters non-negative, sizes), and snapshot serialization contract.
  - Green: Implement data-only VO with basic guards; no crypto yet.
  - Refactor: Align names with protocol, prepare for transitions.
  - Deliverable: `RatchetState` and tests.

- **Step 3: Aggregate skeletons (SecureSession, PendingSession) with invariants only**
  - Red: Tests ensure aggregates can be constructed with required fields; invalid inputs throw; timestamps set via `IClock`.
  - Green: Implement constructors/factories without crypto.
  - Refactor: Extract common validation, add domain events types.
  - Deliverable: Aggregate classes + invariant tests.

- **Step 4: PendingSession behaviors (decision workflow)**
  - Red: State machine tests cover `ApproveAndRespond`, `AutoRespond` policy guard, `Reject`, `Expire` transitions and idempotency.
  - Green: Implement transitions and return placeholder `HandshakeResponse` using `ICryptoPrimitives` stub.
  - Refactor: Event emission, clearer result types.
  - Deliverable: Behavior-complete `PendingSession` + tests.

 - **Step 5: SecureSession behaviors (Encrypt/Decrypt with ratchet framing)**
  - Red: Tests for
    - Counter monotonicity, AD enforcement, skipped-keys retrieval, `TouchLastUsed()` updates.
    - Initiator vs Responder first-message asymmetry:
      - Initiator first-send performs a DH ratchet before first `Encrypt` and emits correct header.
      - Responder decrypts the first inbound message, advances receiving chain, then persists finalized state.
    - First-message envelope carries rendezvous payload (e.g., `SessionId`) enabling responder mapping.
  - Green: Implement via `SessionCrypto` port. API explicit about framing: return/consume a ratchet-framed message (e.g., `SessionRatchetMessage`) rather than a bare `Ciphertext` where appropriate. Update state immutably and persist via repository.
  - Refactor: Remove duplication, improve error messages, tune invariants.
  - Deliverable: `SecureSession` behavior + tests.

  - Slow-path inbound resolution (domain-level)
    - Red: `InboundMessageResolver` tests
      - Fast path: resolves `SessionId` by `IRatchetKeyIndex.TryResolveAsync(header.PreKey)` and decrypts via loaded `SecureSession`.
      - Slow path: when fast path misses, enumerates candidate sessions (via `ISessionCatalog` or repo enumeration) and attempts `SecureSession.Decrypt` until one succeeds; persists updated state; upserts `IRatchetKeyIndex` for the new header key.
      - Returns `(SessionId, Plaintext)` on success; null on miss.
    - Green: Implement resolver using domain ports only; no logging or app concerns.
    - Refactor: Tune iteration order and guard rails (max candidates/time budget) as needed.

 - **Step 6: Domain services (HandshakePlanner, SessionCrypto port)**
  - Red: Planner tests validate initiator vs responder paths for X3DH, including DH1/DH2/DH3 and optional DH4 (one-time pre-key); signature verification and failure cases using crypto stubs.
  - Green: Implement pure planner; define `SessionCrypto` interface aligned with existing utils. Provide an adapter implementation that reuses `X3DHManager` and `CryptoUtils`. No secret logging inside the domain; any diagnostics live in adapters at the Application layer.
  - Refactor: Streamline method names and DTOs.
  - Deliverable: Services + tests.

- **Step 7: Cut-over: delete legacy crypto paths and fix compile to adopt new domain**
  - Red: Identify all compile-time usages of legacy crypto/session code (search references). Create a todo list of broken call sites.
  - Green: Delete legacy paths and fix compile errors by replacing call sites with the new domain/services and ports. Keep behavior surface the same at the app boundary.
  - Refactor: Remove dead code and obsolete adapters; ensure exception types and nullability are consistent.
  - Deliverable: Legacy code removed; solution compiles with new domain abstractions wired.

  Replacement mapping during cut-over:
  - `DoubleRatchetSession` exposure in app code -> `SecureSession` aggregate behavior via repositories.
  - `IDoubleRatchetSessionStore` -> `ISessionRepository` (scoped through Application).
  - `X3dPreKeyBundle` -> canonical `PreKeyBundle` (use adapters during transition).
  - Direct calls to `X3DHManager`/`CryptoUtils` -> `SessionCrypto` adapter via domain services.
  - `IRatchetKeySessionLookup` (Application) -> `IRatchetKeyIndex` (Cryptography domain) used by `InboundMessageResolver`.

  Group crypto compatibility (follow-up mini-phase):
  - Ensure `GroupManager` continues to serialize/deserialize member session state using new VO/DBO mappers if type names change.
  - Align any ratchet message framing names if they diverge (e.g., `SessionRatchetMessage`).

 - **Step 8: Application services integration (HandshakeService, SecureMessagingService)**
  - Red: Tests for user journeys exercising services via ports:
    - Standard outbound initiation (initiator path).
    - Reverse-signal sender: `CreateInvitation(PeerId)` creates an invitation and (optionally) enqueues via outbox.
    - Reverse-signal receiver: `PendingSession.FromInvitation(...)` then `ApproveAndRespond(...)` decrypts first inbound, finalizes session (persist), and enqueues response.
    - Inbound auto-approval per policy.
    - Use real adapters where practical; minimal stubs only for network/clock/random.
  - Green: Implement orchestrators; ensure side-effects: outbox enqueue, notifications, repo updates.
  - Refactor: Split methods if needed, remove duplication.
  - Deliverable: Services integrated at the app layer with tests.

- **Step 9: Persistence (EF Core) and migrations for new tables**
  - Red: Integration tests exercise repos via EF Sqlite file DB matching schemas above; ensure encryption/password path remains intact.
  - Green: Implement EF entities/DBOs, mappings, migrations; wire DI registrations.
  - Refactor: Review indexes, add constraints, clean up migration names.
  - Deliverable: Working persistence with tests; schema aligned with aggregates.

- **Step 10: Transport integration and envelope wiring (Application layer)**
  - Red: End-to-end tests: approve inbound -> enqueue response; standard outbound -> initial cipher produced; encrypt/decrypt roundtrip.
  - Green: Wire adapters to existing transport/envelope without leaking crypto domain internals.
  - Refactor: Improve boundaries, logging at app layer only.
  - Deliverable: E2E paths green; no runtime flags. Roll back via Git if needed.

- **Step 10a: Group crypto alignment (compatibility and naming)**
  - Red: Tests validate GroupManager save/restore flows with new VO constraints; verify re-key flows; ensure no raw `byte[]` in public APIs except agreed exceptions.
  - Green: Align naming/framing (e.g., if `SessionRatchetMessage` references appear), add VO/DBO mappers for group state; keep domain free of secret logging.
  - Refactor: Consolidate serialization boundaries, confirm compatibility with sender-key logic, and update docs/glossary if needed.

- **Step 11: Rollout and cleanup**
  - Red: Contract/backward-compat tests comparing legacy vs new behavior on golden vectors.
  - Green: Remove residual legacy only after parity; use small, compiling commits.
  - Refactor: Consolidate utils into `SessionCrypto` adapter; keep `CryptoUtils` exception policy intact.
  - Deliverable: Finalized switchover with safety.

### Application cleanup (delete/migrate these at the end)
- Percolator.Application.Sessions:
  - `DirectSessionManager` (replace with orchestrator that calls domain `SecureSession` and repos; remove direct `DoubleRatchetSession` usage and state surgery)
  - `IDirectSessionManager` (redefine as thin orchestrator or remove if redundant)
  - `IDirectSessionManager.TryInferAndReceiveAsync` (moved to domain `InboundMessageResolver` fast/slow path)
  - `ConversationService` cryptographic logic (move handshake/session establishment and decrypt-first-message into domain services; keep routing, transport calls, and notifications only)
- Percolator.Application.KeyExchange:
  - `X3DHOrchestrator`, `IX3DHOrchestrator` (replace with `SessionCrypto` adapter implementation wired via Application/Infrastructure)
  - `ISelfPreKeyBundleRepository` (use canonical `PreKeyBundle` VO and appropriate domain/Application ports)
  - `HandshakeResponse` (superseded by domain `HandshakeResponse` VO)
- Replace usages of `IDoubleRatchetSessionStore` with domain `ISessionRepository` (scoped by Application)
- Remove direct references to `X3DHManager` and `CryptoUtils` from Application code; route through domain ports (`SessionCrypto`, `HandshakePlanner`).
 - Percolator.Application.Network:
   - `IRatchetKeySessionLookup` (replace with domain `IRatchetKeyIndex`; maintain a thin adapter during cut-over)
 - Percolator.Infrastructure.Sessions:
   - `RatchetKeySessionLookup` (re-implement as adapter for `IRatchetKeyIndex`; return `SessionId` instead of `DirectSessionId` in domain; Application adapter handles `DirectSessionId` mapping)

---

Status: Phases 1–4 drafted. Roadmap with TDD steps added. Implement via compile-time cut-over; no feature flags.

