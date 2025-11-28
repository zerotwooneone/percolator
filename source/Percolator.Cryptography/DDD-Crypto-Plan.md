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


1) Step 1 – Core VOs/IDs
   - Introduced `SessionId`, `PendingSessionId`, `PeerId`, `Plaintext`, `Ciphertext`, `AssociatedData` with guards and tests; avoided exposing raw `byte[]` in public APIs.

2) Step 2 – RatchetState shape
   - Added `RatchetState` as a persistence-ready VO with basic invariants and snapshot contract; no crypto behavior yet.

3) Step 3 – Aggregate skeletons
   - Created `SecureSession` and `PendingSession` aggregates with constructor invariants and clocked timestamps; crypto behaviors deferred.

4) Step 4 – PendingSession behaviors
   - Implemented decision workflow (`Approve`, `AutoRespond`, `Reject`, `Expire`) returning minimal handshake responses via crypto ports; added tests for state transitions.

5) Step 5 – SecureSession behaviors
   - Implemented Double Ratchet `Encrypt/Decrypt` with ratchet framing and AD rules; enforced counters/skipped-keys; documented initiator/responder asymmetry and added inbound resolver tests (fast/slow path).

6) Step 6 – Domain services (Planner/Crypto)
   - Centralized handshake logic behind `IHandshakePlanner` and `ISessionCrypto` (including responder-side `X3DH_Respond`); provided adapter implementations used by application services.

  - **Step 7: Cut-over: delete legacy crypto paths and fix compile to adopt new domain**
  - Red: Identify all compile-time usages of legacy crypto/session code (search references). Create a todo list of broken call sites.
  - Green: Delete legacy paths and fix compile errors by replacing call sites with the new domain/services and ports. Keep behavior surface the same at the app boundary.
  - Refactor: Remove dead code and obsolete adapters; ensure exception types and nullability are consistent.
  - Deliverable: Legacy code removed; solution compiles with new domain abstractions wired.

  ### Step 7b: Application cut-over inventory and plan (compile without legacy)

  - Purpose
    - Replace all remaining usages of legacy classes/interfaces removed during Step 7 with the new DDD model entry points. No shims or no-ops; we either refactor to the new services or remove and replace the old components.

  - Broken classes (from build + grep)
    - Percolator.Application/KeyExchange
      - X3DHOrchestrator (uses IX3DHManager)
    - Percolator.Application/Network
      - EstablishDirectSessionHandler (uses IX3DHManager)
    - Percolator.Application/Sessions
      - ConversationService (uses IX3DHManager)
      - DirectSessionManager (uses IDoubleRatchetSessionStore)
    - Percolator.Application/Apps/Chat
      - TransportKeyResolver (uses IDoubleRatchetSessionStore)
      - IGroupManagerResolver (references legacy GroupManager)
      - DefaultGroupManagerResolver (references legacy GroupManager)
      - PersistentGroupManagerResolver (references legacy GroupManager)
      - GroupKeyOperations (references legacy GroupManager)

  - Cut-over plan per class
    - X3DHOrchestrator
      - Replace with HandshakeService (Step 8) that orchestrates PendingSession/SecureSession using SessionCrypto port.
      - Action: Delete X3DHOrchestrator; update call sites to use IHandshakeService.
    - EstablishDirectSessionHandler
      - Replace IX3DHManager usage with IHandshakeService.InitiateStandardHandshake(peerId,...).
      - Action: Refactor handler to depend on IHandshakeService; remove IX3DHManager.
    - ConversationService
      - Split responsibilities: handshake flows -> IHandshakeService; message send/recv -> ISecureMessagingService.
      - Action: Refactor to depend on the two services; remove direct IX3DHManager/DR usage.
    - DirectSessionManager
      - Superseded by ISecureMessagingService and InboundMessageResolver (domain-level fast/slow path).
      - Action: Remove class; update callers to ISecureMessagingService and the inbound resolver path.
    - TransportKeyResolver
      - Superseded by IRatchetKeyIndex + InboundMessageResolver.
      - Action: Remove; wire inbound decryption through resolver.
    - IGroupManagerResolver / DefaultGroupManagerResolver / PersistentGroupManagerResolver / GroupKeyOperations
      - Replace legacy GroupManager with PrivateGroup aggregate; persistence via normalized tables (GroupManagerStates columnar, GroupMembers, SenderKeys) added in Step 7a.
      - Action: Introduce an Application adapter over the new DBOs (e.g., IPrivateGroupRepository in Application using Infrastructure DbContext) and expose minimal operations needed by Chat app (create group, emit/apply changes, read state). Delete legacy resolvers and operations; update call sites to use the new adapter that composes PrivateGroup aggregate methods.

  - DI and persistence adjustments
    - Remove DI registrations for deleted legacy stores/services (done for blob repos in 7a).
    - Add DI for new services in Step 8 (IHandshakeService, ISecureMessagingService) and repositories (ISessionRepository, IPendingSessionRepository, IRatchetKeyIndex) when implemented.

  - Migration note
    - EF migration for the new group tables is deferred until the Application builds (tracked as Step 7c).

  - Exit criteria for 7b
    - Percolator.Application compiles with legacy classes removed and updated to the new service boundaries, even if some services are not yet implemented (they will be provided in Step 8). No shims/no-ops introduced.

  Replacement mapping during cut-over:
  - `DoubleRatchetSession` exposure in app code -> `SecureSession` aggregate behavior via repositories.
  - `IDoubleRatchetSessionStore` -> `ISessionRepository` (scoped through Application)
  - `X3dPreKeyBundle` -> canonical `PreKeyBundle` (use adapters during transition)
  - Direct calls to `X3DHManager`/`CryptoUtils` -> `SessionCrypto` adapter via domain services.
  - `IRatchetKeySessionLookup` (Application) -> `IRatchetKeyIndex` (Cryptography domain) used by `InboundMessageResolver`.

  Group management cut-over (added in Step 7a work):
  - Replace legacy `GroupManager` with `PrivateGroup` aggregate and signed membership change flows.
  - Persistence port `IPrivateGroupStateStore` defined in Cryptography; Infrastructure implementation `SqlitePrivateGroupStateStore` registered in DI.
  - Plan a DB migration if schema changes are needed for group state storage (see 7a/10a Infra: SQLite repository & migration).
  - Application layer will orchestrate distribution of group changes over pairwise `SecureSession` (to be wired in Step 8), but compile fixes begin here by removing legacy references and adding DI wiring.

  Application build fixes to complete Step 7:
  - Remove remaining references to legacy `GroupManager`/`DoubleRatchetSession` in `Percolator.Application`.
  - Inject and use `IPrivateGroupStateStore` via Infrastructure DI.
  - Ensure `AddInfrastructureServices` includes `AddCryptographyInfrastructure` so `SqlitePrivateGroupStateStore` is available.
  - Update any application workflows that previously depended on legacy group/session code to the new domain entry points (pending Step 8 orchestration where needed) so the solution compiles.

 - **Step 7a: Group management (Signal-style) planning and TDD roadmap**
  - Context
    - We will replace the legacy `GroupManager` that depended on `DoubleRatchetSession` with a DDD-aligned design based on Signal private groups. See `group-management.md` for protocol behavior and persistence model.
    - Control-plane messages (invites, membership changes, rekeys) are delivered 1:1 over pairwise `SecureSession`. Data-plane for group payloads uses a `SenderKeySession`-style primitive.
  - Scope for this step
    - Planning and test scaffolding only; implementation begins after Step 7 build is green.
  - TDD milestones (large but independent chunks)
    1) Red: Genesis/Invitation Roundtrip
       - Creator creates a group (GroupId, MasterKey, initial state) and emits a signed invite payload.
       - Member accepts: verifies signature, initializes local aggregate state and SenderKey material.
       - Transport simulated via `SecureSession` Encrypt/Decrypt between creator and member.
    2) Green: Minimal implementation to pass genesis/invitation tests
       - New aggregate `PrivateGroup` (or `GroupManagerV2`) with:
         - CreateGroup, CreateInvitePayload, AcceptInvite.
         - Serialization (Save/Load) using at-rest crypto via `CryptoUtils`.
    3) Red: Add Member change
       - Admin emits signed AddMember command (sequence increment).
       - Recipients verify admin role and apply change deterministically.
    4) Green: Implement AddMember flow
       - Command VO + signature verification + state mutation.
    5) Red: Remove Member + Sender Key Rotation
       - Admin emits RemoveMember; recipients apply state.
       - Admin generates new sender key material and distributes only to remaining members via pairwise `SecureSession`.
       - Ensure removed member cannot decrypt subsequent group messages.
    6) Green: Implement Remove + rotation
       - Rotation primitive and distribution payload; local replacement of sender key material.
    7) Red: Sequencing and replay rules
       - SequenceNumber monotonic; out-of-order buffering or rejection policy covered by tests.
    8) Green: Enforce sequence and idempotency
    9) Red: Save/Load state roundtrip
      - Persist group metadata, members, action log (optional), sender keys (mine and theirs).
    10) Green: Implement persistence mappers (domain->DBO) (can stub during initial unit tests, full EF integration in Step 9)
    10a) Infra: SQLite repository & migration
      - Implement a Sqlite `IPrivateGroupStateStore` backed by existing `GroupManagerStates` (or new table if needed).
      - Create and apply an EF Core migration if schema changes are required to support new fields.
      - Wire DI registration and add an integration test to verify Save/Get roundtrip using a temp SQLite DB.
  - Deliverables
    - New DDD group management aggregate and value objects.
    - Tests for invite, add/remove, rekey, sequencing, and state persistence (unit-level; EF integration later).
    - No dependency on legacy `DoubleRatchetSession`.

 - **Step 8: Application services integration (HandshakeService, SecureMessagingService)**
  - Red: Tests for user journeys exercising services via ports (aligned to session-flow):
    - Standard outbound initiation (initiator path):
      - Fetch pre-key bundle, derive IRK, persist a short‑lived prehandshake record containing only the encrypted IRK and the remote identity key SPKI hash; DO NOT persist the initiator’s ephemeral private key.
      - Send initiator hello/pre-key message if applicable.
      - On first responder ratchet message, decrypt inner payload to obtain the responder-assigned `session_id`; finalize the initiator session and delete the prehandshake record.
    - Reverse-signal sender: `CreateInvitation(PeerId)` creates an invitation (minimal metadata) and may enqueue via outbox.
    - Reverse-signal receiver: upon accept, act as responder immediately, establish and send the first ratchet message; inviter finalizes using the decrypted inner payload’s `session_id`.
    - Inbound auto-approval per policy.
    - Use real adapters where practical; minimal stubs only for network/clock/random.
  - Green: Implement orchestrators; ensure side-effects per session-flow: outbox enqueue, notifications, repo updates; prehandshake TTL/purge and index on `(SelfIdentityId, RemoteIdentityKeySpkiHash, CreatedAtUtc)`.
  - Refactor: Split methods if needed, remove duplication.
  - Deliverable: Services integrated at the app layer with tests.

  - **Step 8a: Prepare to delete ConversationService (and its tests)**
    - Purpose
      - Remove the legacy monolith `ConversationService` by migrating its call sites to DDD-aligned services without shims or no-op placeholders.
      - Do NOT use MediatR for low-level protocol steps (X3DH/DR). Reserve MediatR only for high-level user actions.
    - Responsibilities to replace
      - Session lookup/mapping (remote peer -> `DirectSessionId`) scoped by self-identity
      - Outbound session establishment (standard initiator path)
      - Transport round-trips for initial session request/response (gRPC)
      - First-message decrypt/finalize (initiator) and persist
    - Replacement services/ports
      - `IDirectSessionRepository` (Network) via a thin app adapter if needed for self-identity scoping (name: `IDirectSessionLocator`)
      - `IHandshakeService` for session establishment using crypto-domain types (`CryptoPeerId`, `SessionId`, `SessionRatchetMessage`)
      - `ISecureMessagingService` for encrypt/decrypt and ratchet state progression
      - `IGrpcSessionService` for the gRPC establish-session round-trip
      - Explicit type aliases where multiple PeerId types exist: `CryptoPeerId`, `IdentityPeerId`, `NetworkPeerId`
    - Call sites to migrate (replace `IConversationService` usage)
      - `Percolator.Application.Cli.ConnectToPeerHandler`
      - `Percolator.Application.Cli.DhtProbeHandler`
      - `Percolator.Application.Cli.DhtPingHandler`
      - `Percolator.Application.Cli.InitiateHandshakeViaHostHandler`
      - `Percolator.Application.Cli.RequestPreKeyBundleByPkhHandler`
      - `Percolator.Application.Cli.SubmitPreKeysHandler`
      - Tests under `Percolator.ApplicationTests` that mock `IConversationService`
    - Migration plan (TDD)
      1. Add `IDirectSessionLocator` (app service) with tests
         - `Task<DirectSessionId?> GetAsync(NetworkPeerId remote, int selfIdentityId, CancellationToken ct)`
         - Tests: returns existing id; returns null when missing
      2. Update CLI handlers listed above
         - Replace `GetExistingDirectSessionAsync` with `IDirectSessionLocator`
         - When a new session is required, call `IHandshakeService` directly (no MediatR), then persist mapping via `IDirectSessionRepository`
         - Keep transport use (`IGrpcSessionService`, `IMessageTransportService`) as-is
         - Tests: reuse session when present; establish via handshake and persist when missing
      3. Replace initiator finalize paths to use `ISecureMessagingService` for decrypting first responder message and commit the finalized session id
      4. Delete `ConversationService`, `IConversationService`, DI registration, and all `ConversationService` tests
    - Per-callsite migration details (flows, required ports, test matrix)
      - ConnectToPeerHandler
        - Flow
          1) Resolve `IdentityPeerId` from name via `IPeerIdentityRepository`
          2) Use `IDirectSessionLocator.GetAsync(NetworkPeerId, selfIdentityId)`
          3) If missing: call `IHandshakeService.InitiateStandardHandshake(CryptoPeerId)` -> `(SessionId, SessionRatchetMessage? initial)`
          4) Persist mapping via `IDirectSessionRepository.UpsertAsync(NetworkPeerId, DirectSessionId, selfIdentityId)`
          5) Return `DirectSessionId`
        - Tests
          - Reuses existing session
          - Establishes new session, persists mapping, returns new id
          - Errors: missing identity, handshake failure -> throws
        - Ports
          - `IPeerIdentityRepository`, `IDirectSessionLocator`, `IHandshakeService`, `IDirectSessionRepository`
      - DhtProbeHandler
        - Flow
          1) Resolve remote peer via `IPeerIdentityRepository`
          2) Ensure direct session id via locator or establish via `IHandshakeService` as above
          3) Build `InternalEnvelope` (PingRequest)
          4) Use `IMessageService.SendMessageAsync(envelope, IdentityPeerId)` (no response expected)
        - Tests
          - Sends ping over existing session
          - Establishes missing session and then sends
          - Error when identity or keys are missing
        - Ports
          - `IPeerIdentityRepository`, `IDirectSessionLocator`, `IHandshakeService`, `IMessageService`
      - DhtPingHandler
        - Flow (no auto-establish in current behavior):
          1) Resolve remote peer
          2) Require existing `DirectSessionId` from locator; if null, throw (preserve behavior)
          3) Build Ping envelope; call `IMessageService.SendMessageAsync`
        - Tests
          - Happy path sends ping
          - Throws when direct session not found
        - Ports
          - `IPeerIdentityRepository`, `IDirectSessionLocator`, `IMessageService`
      - InitiateHandshakeViaHostHandler
        - Flow
          1) Ensure direct session to Host via locator (throw if missing, preserve behavior)
          2) Get target PKH pre-key bundle via Host (encrypt/send via `ISecureMessagingService` + `IMessageTransportService`)
          3) Validate bundle fields; activate PKH via `IPeerPublicSigningKeyStore`
          4) Upsert routing profile with identity public key
          5) Enqueue initiator hello via `IInitiatorHelloService` (existing service)
        - Tests
          - Retrieves bundle and enqueues hello via host
          - Fails when host session missing, bundle malformed, or decrypt fails
        - Ports
          - `IDirectSessionLocator`, `ISecureMessagingService`, `IMessageTransportService`, `IPeerPublicSigningKeyStore`, `IPeerRoutingProfileRepository`, `IInitiatorHelloService`
      - RequestPreKeyBundleByPkhHandler
        - Flow
          1) Resolve Host identity and require existing direct session via locator (throw if missing)
          2) Build GetPreKeyBundle request envelope
          3) Encrypt via `ISecureMessagingService`, send via `IMessageTransportService`
          4) Decrypt response via `ISecureMessagingService`, validate type and fields
          5) (Handshake will be performed by higher-level flow; this handler returns after fetch)
        - Tests
          - Happy path fetches bundle and returns Unit
          - Errors on missing host session, no response payload, or decrypt failure
        - Ports
          - `IDirectSessionLocator`, `ISecureMessagingService`, `IMessageTransportService`
      - SubmitPreKeysHandler
        - Flow
          1) Resolve target via `IPeerIdentityRepository`
          2) Require existing direct session via `IDirectSessionLocator` (no auto-establish). Throw if null
          3) Generate and persist signed pre-key and N one-time keys via `ISelfPreKeyBundleRepository`
          4) Build `SubmitPreKeyBundleRequest` in `InternalEnvelope`
          5) Encrypt via `ISecureMessagingService` and send via `IMessageTransportService`
          6) Decrypt response and verify `SubmitPreKeyBundleResponse`
          7) Return 0 on success; non-zero/throw on contract violations
        - Tests
          - Happy path returns 0; transport/send invoked once; decrypt invoked once
          - Throws on zero count
          - Throws on missing direct session
          - Returns error/non-zero or throws on unexpected response type or decrypt failure (align with current behavior)
        - Ports
          - `IDirectSessionLocator`, `ISelfPreKeyBundleRepository`, `ISecureMessagingService`, `IMessageTransportService`, `IPeerIdentityRepository`
    - Error handling and contracts
      - Preserve current behavior where some handlers require an existing direct session and do not auto-establish (DhtPingHandler, RequestPreKeyBundleByPkhHandler)
      - For flows that may establish (ConnectToPeerHandler, DhtProbeHandler), propagate `InvalidOperationException` with meaningful messages on failure
      - Unit tests assert thrown exceptions and messages where applicable (contracts-first)
    - Acceptance criteria (Step 8a complete)
      - All listed call sites compile and tests pass using `IDirectSessionLocator`, `IHandshakeService`, `ISecureMessagingService`, and `IMessageTransportService`
      - `ConversationService` and `IConversationService` removed; DI updated; no references remain
      - Tests cover: reuse vs establish, transport send, decrypt/validate response, and failure cases per handler contract
    - DI changes
      - Remove registration for `IConversationService`
      - Add registration for `IDirectSessionLocator` (app layer)
      - Ensure `IHandshakeService`, `ISecureMessagingService`, and `IGrpcSessionService` are already registered
    - Testing guidelines
      - Follow `unit-testing.md`: AAA structure; verify observable behavior and side-effects only (e.g., repo upserts, transport calls)
      - Use loose mocks; avoid ordering assertions unless part of the contract
      - No shims/no-ops; update call sites and tests as services are introduced

  - Sub-step: Cutover inbound decryption call sites
    - Replace usages of `DirectSessionManager.ReceiveMessageAsync(SessionId, SessionRatchetMessage)` with Application composition over domain `ISecureMessagingService.DecryptInboundAsync` (fast/slow path) that does not require a known session id; initiator finalize path must parse `session_id` from the responder’s inner payload and use the responder’s header `public_ratchet_key`.
    - References to update:
      - Percolator.ApplicationIntegrationTests/Dht/DhtProbeLoopbackTests.cs
      - Percolator.ApplicationIntegrationTests/Dht/DhtIntegrationTests.cs
      - Percolator.ApplicationTests/Handshake/HandshakeInitiatorFlowTests.cs
      - Percolator.ApplicationTests/Handshake/DirectSessionManagerHandshakeTests.cs
      - Percolator.ApplicationTests/Sessions/SessionMessageTests.cs
      - Percolator.ApplicationTests/Sessions/DirectSessionManagerTests.cs

### Step 8b: Crypto service decomposition and TDD hardening

- **Objective**
  - Decompose `ISessionCrypto` responsibilities into smaller units that improve testability, align tightly with session-flow.md, and avoid application-layer duplication. Implement concrete classes and tests using TDD per unit-testing.md.

- **New Interfaces (Cryptography domain)**
  - `IX3dhDeriver`
    - Derives the Initial Root Key (IRK) for initiator/responder flows.
    - Inputs: IK/EPK private for local side, IK/SPK/OPK public for remote side.
    - Output: 32-byte IRK and initiator ephemeral public (for initiator path).
    - Suggested signature(s):
      - Initiator: `(SharedSecret irk, RatchetEphemeralKey initiatorEphemeralPublic) DeriveInitiator(RatchetIdentityKey remoteIk, PreKey remoteSpk, OneTimeKey? remoteOtk, PrivatePreKey localIkPriv)`
      - Responder: `SharedSecret DeriveResponder(RatchetIdentityKey initiatorIk, RatchetEphemeralKey initiatorEk, PrivatePreKey localIkPriv, PrivatePreKey localSpkPriv, PrivatePreKey? localOtkPriv)`
  - `IPreKeyBundleValidator`
    - Validates bundle shape, SPK signature, freshness/expiry.
    - Suggested signature(s):
      - `void Validate(PreKeyBundle bundle)` (throws on invalid)
  - `IRatchetEngine`
    - Encapsulates Double Ratchet transitions (encrypt/decrypt), chain key advancement, DH ratchet step.
    - Suggested signature(s):
      - `(
          Ciphertext ct,
          RatchetEphemeralKey headerKey,
          RatchetState newState
        ) Encrypt(RatchetState state, Plaintext pt, AssociatedData ad, ulong counter)`
      - `(
          Plaintext pt,
          RatchetState newState
        ) Decrypt(RatchetState state, SessionRatchetMessage framed, AssociatedData ad)`
      - `RatchetState DhRatchetingStep(RatchetState state, RatchetEphemeralKey remotePublic)`
  - `ISecureRandom`
    - RNG abstraction used for ephemeral key generation and random nonces (where applicable).
    - Suggested signature(s): `void Fill(byte[] buffer)`; `byte[] GetBytes(int count)`
  - `IKeyProtector`
    - Encrypt-at-rest for IRK/session material (thin port over CryptoUtils methods).
    - Suggested signature(s):
      - `byte[] Protect(byte[] masterKey, byte[] data, byte[]? ad = null)`
      - `byte[] Unprotect(byte[] masterKey, byte[] payload, byte[]? ad = null)`
  - `IEphemeralKeyFactory`, `IAssociatedDataSerializer` if test pain emerges.
  - `IEphemeralKeyFactory` signatures: `PrivateEphemeralKey Create(); RatchetEphemeralKey ToPublic(PrivateEphemeralKey priv)`
  - `IAssociatedDataSerializer` signatures: `byte[] SerializeHeader(RatchetEphemeralKey key, ulong counter, ulong prevLen); byte[] SerializeWithAd((RatchetEphemeralKey, ulong, ulong) header, byte[] ad)`

- **Implementations**
  - `AeadSessionCrypto` delegates to:
    - `IX3dhDeriver` for X3DH_Initiate (return `(SharedSecret, RatchetEphemeralKey)`).
    - `IRatchetEngine` for DR_Encrypt/DR_Decrypt (returns `(Ciphertext, HeaderKey, NewState)` / `(Plaintext, NewState)`).
    - `IPreKeyBundleValidator` for SPK signature and expiry.
    - `ISecureRandom` for any randomness (kept minimal).
    - `IKeyProtector` for at-rest operations used by repositories (where applicable).

- **TDD Plan**
  - Red: Add focused tests
    - X3DH
      - `X3DH_Initiate_WithInvalidSignature_Throws` (already added).
      - `X3DH_Initiate_Derives_IRK_And_Returns_Ephemeral` (vector-less: asserts 32-byte IRK and non-empty SPKI).
      - `DeriveResponder_Mirrors_Initiator_On_Valid_Inputs` (IRK equality property test using same key inputs).
    - Double Ratchet
      - `EncryptDecrypt_WithSameAssociatedData_Roundtrips` (already in place, ensure chain keys initialized).
      - `Decrypt_WithDifferentAssociatedData_Throws` (auth failure).
      - `Decrypt_WithTamperedCiphertext_Throws`.
      - `DhRatchetingStep_Updates_RootKey_And_Resets_ReceivingChain`
  - Green: Implement minimal logic in `IX3dhDeriver`, `IRatchetEngine` and wire into `AeadSessionCrypto`.
  - Refactor: Improve cohesion; ensure no key material is logged; zeroize temporaries where feasible.

- **Acceptance Criteria**
  - Tests in `Percolator.CryptographyTests` pass for X3DH and DR flows.
  - `AeadSessionCrypto` contains no ad-hoc randomness outside `ISecureRandom` and uses stable AD serialization.
  - No duplication of crypto interfaces in Application. Application calls domain interfaces only.

- **Follow-ups**
  - Add golden test vectors when available.
  - Integrate with `SecureSession`/`InboundMessageResolver` and repositories for end-to-end behaviors.

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

## Step 12: Identity-scoped repository alignment (EF Core)

- **Objective**
  - Ensure all repositories/adapters touching tables keyed by `SelfIdentityId` operate within an identity-scoped DbContext (via `ActiveIdentityContext`) and respect strict global filters.

- **Tables with `SelfIdentityId` (from PercolatorDbContext)**
  - SelfPreKeySigned (`SelfPreKeySignedDbo`)
  - SelfOneTimePreKeys (`SelfOneTimePreKeyDbo`)
  - PreHandshakeSessions (`PreHandshakeSessionDbo`)
  - SelfIdentityKeys (`SelfIdentityKeysDbo`)
  - SelfIdentityKnownPeer (`SelfIdentityKnownPeerDbo`)
  - DirectSession (`DirectSessionDbo`)
  - DoubleRatchetSessions (`DoubleRatchetSessionDbo`)
  - SkippedMessageKeys (`SkippedMessageKeyDbo`)
  - RatchetKeyIndex (`RatchetKeyIndexDbo`)
  - Conversations (`ConversationDbo`)
  - DirectSessionConversations (`DirectSessionConversationDbo`)

- **Actions**
  - Update DI: `PercolatorDbContext` constructor receives `ActiveIdentityContext`; global filters applied for per-identity entities.
  - Repos/adapters for the above entities should:
    - Avoid passing/guessing `SelfIdentityId`; obtain identity from `ActiveIdentityContext` and rely on global filters.
    - Use strict filtering semantics (no results when active identity is unset).
  - Tests: construct DbContext with `ActiveIdentityContext` and seed `SelfIdentityDbo` matching the test `SelfIdentityId`.
  - Migration review: confirm indexes exist for identity + remote SPKI hash + created_at.

- **Deliverable**
  - All identity-scoped repositories consistently use the scoped DbContext; integration tests green.

