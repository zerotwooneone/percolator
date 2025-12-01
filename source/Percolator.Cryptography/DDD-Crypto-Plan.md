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
 
- Summary: Identified journeys (standard outbound, reverse-signal inbound). Defined core aggregates (`SecureSession`, `PendingSession`), essential VOs (IDs, payloads, policies), and ports (`HandshakePlanner`, `SessionCrypto`, repos). Established domain isolation and public-API byte policy.

---

Next: Phase 2 – New Implementation Plan

---

## Phase 2: New Implementation Plan
 
- Summary: Implemented aggregates/VOs with guards and timestamps; added `RatchetState`. Introduced `HandshakePlanner` and `ISessionCrypto` ports (planner has an app adapter). Deferred orchestrators to Application.

---

Next: Phase 3 – Database Refactoring Plan

---

## Phase 3: Database Refactoring Plan
 
- Summary: Sketched `Sessions` and `PendingSessions` schemas. Implemented `SqlitePendingSessionRepository`; sessions persistence/migrations to follow during app integration.

---

Next: Phase 4 – Application-Layer Migration Plan

---

## Phase 4: Application-Layer Migration Plan
 
- Summary: Cataloged legacy call sites. Defined migration to `HandshakeService` and `SecureMessagingService`, replacing direct crypto usages. Adopt test-first cut-over for outbound/inbound flows and encrypt/decrypt paths.

---

## Implementation roadmap (AI-sized steps with TDD)


1) Step 1 — Core VOs/IDs (completed)
   - Added IDs and payload VOs (`SessionId`, `PendingSessionId`, `PeerId`, `Plaintext`, `Ciphertext`, `AssociatedData`) with guards and tests.

2) Step 2 — RatchetState (completed)
   - Introduced a persistence-ready `RatchetState` with basic invariants and snapshot contract.

3) Step 3 — Aggregates (completed)
   - Added `SecureSession` and `PendingSession` skeletons with constructor invariants and clocked timestamps.

4) Step 4 — PendingSession behaviors (completed)
   - Implemented `Approve`, `AutoRespond`, `Reject`, `Expire` minimally and covered with tests.

5) Step 5 — SecureSession behaviors (completed)
   - Implemented baseline `Encrypt/Decrypt` and inbound resolver scaffolding (counters/skipped-keys enforced).

6) Step 6 — Domain services (completed)
   - Added `IHandshakePlanner` and `ISessionCrypto` with an application adapter for the planner.

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

### TDD Chunks (AI-executable, sequential, technically detailed)

- **Chunk 1 — Identity-scoped EF alignment**
  - Scope: Ensure all identity-scoped entities are filtered by `ActiveIdentityContext` and no callers pass raw `SelfIdentityId`.
  - Red (tests):
    - Create `Percolator.Infrastructure.Tests/Identity/ActiveIdentityFilteringTests.cs`.
    - Tests:
      - When `ActiveIdentityContext` is unset, queries on identity-scoped sets return zero rows.
      - When set to `X`, only rows for `X` are returned; rows for `Y` excluded.
      - Repos that currently accept `selfIdentityId` ignore the parameter and respect filters.
  - Green (impl changes):
    - `Percolator.Infrastructure/Persistence/PercolatorDbContext`:
      - Inject `ActiveIdentityContext` (singleton or scoped).
      - Apply `HasQueryFilter(e => e.SelfIdentityId == _active.SelfIdentityId)` to identity-scoped DBOs:
        - `SelfPreKeySignedDbo`, `SelfOneTimePreKeyDbo`, `PreHandshakeSessionDbo`, `SelfIdentityKeysDbo`, `SelfIdentityKnownPeerDbo`, `DirectSessionDbo`, `DoubleRatchetSessionDbo`, `SkippedMessageKeyDbo`, `RatchetKeyIndexDbo`, `ConversationDbo`, `DirectSessionConversationDbo`.
    - Remove/obsolete overloads taking explicit `selfIdentityId` in Infrastructure repos; use context instead.
  - Refactor:
    - Update DI composition where `PercolatorDbContext` is created to supply `ActiveIdentityContext`.
    - Replace call sites passing `selfIdentityId` with none; rely on filters.
  - DI changes:
    - Ensure `ActiveIdentityContext` is registered scoped and set during user selection/login flows.
  - Acceptance checks:
    - All tests in the new test file green.
    - Manual sanity: app runs; queries reflect current identity switch.

- **Chunk 2 — Sessions persistence (ISessionRepository + EF)**
  - Scope: Introduce `Sessions` table and repository to persist `SecureSession` aggregate state.
  - Red (tests):
    - Create `Percolator.Infrastructure.Tests/Cryptography/SqliteSessionRepositoryTests.cs`.
    - Tests:
      - `AddAsync` persists with proper identity scoping; `GetAsync` returns same state.
      - `UpdateAsync` changes counters/state; `LastUsedAtUtc` progresses.
      - Rejects cross-identity access due to filters.
  - Green (impl changes):
    - Add DBOs under `Percolator.Infrastructure/Persistence/Dbos/Cryptography`:
      - `SessionDbo` with columns:
        - `SelfIdentityId int`, `SessionId Guid`, `RemotePeerId Guid`, `ProtocolVersion int`, `RootKey blob`, `SendChainKey blob`, `SendCounter ulong`, `RecvChainKey blob`, `RecvCounter ulong`, `AssociatedData blob?`, `CreatedAtUtc long`, `LastUsedAtUtc long`.
      - Configure in `PercolatorDbContext` with `HasKey(SessionId, SelfIdentityId)` and indexes on `(SelfIdentityId, RemotePeerId)`.
    - Mapping helpers:
      - `SessionMapper`: `SecureSession <-> SessionDbo` (serialize `RatchetState` parts; no secrets logged).
    - Implement `SqliteSessionRepository : ISessionRepository` in `Percolator.Infrastructure/Cryptography`:
      - `AddAsync`, `GetAsync`, `UpdateAsync` using DbContext; respect `ActiveIdentityContext`.
    - Create EF migration `AddSessions` with schema above.
    - Register repository in Infra DI (e.g., `AddCryptographyInfrastructure`).
  - Refactor:
    - Ensure `RatchetState` snapshot serialization is centralized and reused.
  - Acceptance checks:
    - Tests green; migration applies; CRUD works in a temp file DB.

- **Chunk 3 — Crypto service decomposition**
  - Scope: Split `ISessionCrypto` internals into smaller ports and provide minimal implementations; wire into `AeadSessionCrypto`.
  - Red (tests):
    - New tests in `Percolator.CryptographyTests`:
      - `IX3dhDeriverTests`: derives 32-byte IRK; initiator returns ephemeral pub; responder matches initiator IRK with shared inputs.
      - `IRatchetEngineTests`: `Encrypt/Decrypt` roundtrip with same AD; tampered AD fails; tampered ciphertext fails; `DhRatchetingStep` updates root key and resets receiving chain.
      - `PreKeyBundleValidatorTests`: throws on missing SPK sig or expired timestamp.
  - Green (impl changes):
    - Define interfaces in `Percolator.Cryptography`:
      - `IX3dhDeriver`, `IRatchetEngine`, `IPreKeyBundleValidator`, `ISecureRandom`, optionally `IAssociatedDataSerializer`, `IEphemeralKeyFactory`.
    - Implement minimal versions in `Percolator.Cryptography` or `Percolator.Infrastructure.Cryptography` (depending on existing patterns):
      - `X3dhDeriver`, `RatchetEngine`, `PreKeyBundleValidator`, `SecureRandom`.
    - Update `AeadSessionCrypto` to depend on the new interfaces and delegate work.
  - Refactor:
    - Remove duplicated crypto pathways; keep `CryptoUtils` public as agreed but not referenced by Application directly.
  - Acceptance checks:
    - All new unit tests pass; `AeadSessionCrypto` compiles with new dependencies.

- **Chunk 4 — Application services (minimal)**
  - Scope: Introduce orchestrators for handshakes and messaging at the Application layer.
  - Red (tests):
    - In `Percolator.ApplicationTests/Handshake/HandshakeServiceTests.cs`:
      - `InitiateFromInvitation_AddsPending_PublishesNotification` (if notifications used) or returns id.
      - `ApproveSession_PersistsSession_DeletesPending_EnqueuesResponse`.
      - `InitiateStandardHandshake_ReturnsSession_OptionalInitialCipher`.
    - In `Percolator.ApplicationTests/Sessions/SecureMessagingServiceTests.cs`:
      - `Encrypt_UpdatesSessionState_Persists`.
      - `Decrypt_UpdatesSessionState_Persists`.
  - Green (impl changes):
    - Create `HandshakeService` and `SecureMessagingService` with interfaces; wire domain ports `ISessionCrypto`, `ISessionRepository`, `IPendingSessionRepository`, `IRatchetKeyIndex`, `IClock`, `IOutbox`.
    - Ensure identity scoping comes from DI.
  - Refactor:
    - Extract mapping DTOs if necessary; avoid leaking domain internals to transport.
  - Acceptance checks:
    - All service tests green; DI registrations present in `AddApplicationServices`.

- **Chunk 5 — Inbound decrypt resolver cut-over**
  - Scope: Replace legacy inbound decryption path with Application adapter over domain `InboundMessageResolver`.
  - Red (tests):
    - Update existing inbound tests to call `ISecureMessagingService.DecryptInboundAsync` without known session id.
    - Assert: fast-path uses `IRatchetKeyIndex`; slow-path tries sessions until decrypt succeeds; state persisted.
  - Green (impl changes):
    - Implement adapter in Application composing `InboundMessageResolver` + repos.
    - Replace `DirectSessionManager.ReceiveMessageAsync` usages in code.
  - Refactor:
    - Remove unused `DirectSessionManager` entry points (actual deletion postponed to cleanup step).
  - Acceptance checks:
    - All updated tests green; no compile references to old receive method.

- **Chunk 6 — Direct session locator + call-site cut-over**
  - Scope: Replace remaining call sites to use new services and a `IDirectSessionLocator`.
  - Red (tests):
    - For each handler (`ConnectToPeerHandler`, `DhtProbeHandler`, `DhtPingHandler`, `RequestPreKeyBundleByPkhHandler`, `SubmitPreKeysHandler`): tests for reuse vs establish flows, error contracts preserved.
  - Green (impl changes):
    - Implement `IDirectSessionLocator` in Application (adapter over infra repo mapping Network peer -> SessionId).
    - Refactor handlers to depend on `IDirectSessionLocator`, `IHandshakeService`, `ISecureMessagingService`.
  - Refactor:
    - Update DI registrations; remove `ConversationService` crypto responsibilities.
  - Acceptance checks:
    - All handler tests pass; `ConversationService` only routes/coordinates transport (no crypto).

- **Chunk 7 — Transport/envelope integration (E2E)**
  - Scope: End-to-end flows through transport with new services.
  - Red (tests):
    - E2E tests for: inbound approval -> outbox enqueue; outbound establish -> initial cipher; simple message roundtrip over `SecureSession`.
  - Green (impl changes):
    - Wire `IMessageTransportService` calls in Application after encrypt/decrypt; ensure envelope types unchanged.
  - Refactor:
    - Boundary/logging polish; avoid secret logging.
  - Acceptance checks:
    - E2E tests green; manual smoke works.

- **Chunk 8 — Rollout and cleanup**
  - Scope: Parity checks and legacy deletion.
  - Red (tests):
    - Back-compat tests comparing old vs new for a curated set of flows.
  - Green (impl changes):
    - Remove residual legacy code paths as per inventory below.
  - Refactor:
    - Consolidate any adapter/util leftovers; docs update.
  - Acceptance checks:
    - Build remains green after deletions; integration tests pass.

## Legacy removal inventory and deletion timing (user-executed deletions)

- **Code (delete when indicated):**
  - Conversation layer
    - `ConversationService`, `IConversationService` — delete after Chunk 6 (handlers refactored and tests green).
  - Sessions (legacy)
    - `DirectSessionManager`, `IDirectSessionManager`, `TryInferAndReceiveAsync` — delete after Chunk 5 (inbound decrypt cut-over complete).
  - Key exchange (legacy)
    - `X3DHOrchestrator`, `IX3DHOrchestrator` — delete after Chunk 4 (HandshakeService in place) and call sites updated in Chunk 6.
  - Stores/adapters (legacy)
    - `IDoubleRatchetSessionStore` usages — remove after Chunk 2 (ISessionRepository live) and Chunk 6 cut-over.
    - `IRatchetKeySessionLookup` (App) and infra implementation — replace with domain `IRatchetKeyIndex` adapter by Chunk 5; delete after tests pass.
  - Group manager legacy (if present)
    - Old resolvers/adapters — schedule after transport/E2E stabilization (post Chunk 7).

- **Tables (drop via migration when indicated):**
  - `DoubleRatchetSessions`, `SkippedMessageKeys` — drop after Chunk 7 once `Sessions` fully replaces state and tests pass.
  - `DirectSession` — drop after Chunk 6 when locator + repos replace usage.
  - `Conversations`, `DirectSessionConversations` — drop after Chunk 7 when transport/envelope integration no longer references them.
  - Keep: `RatchetKeyIndex` (domain uses it), identity-key tables, and pre-key tables.

Note: I will call out in PRs when each deletion point is reached; you can perform the actual removal/migration at those times.
