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
3) Implement a Sqlite-backed `IPeerIdentityRepository` using `PercolatorDbContext` (fresh DB).
   - API: `GetByIdAsync`, `GetByNameAsync`, `FindByPublicKeyHashAsync`, `SaveAsync` (optimistic concurrency `Version`).
   - Repository class (infra): `Percolator.Infrastructure/Repositories/SqlitePeerIdentityRepository.cs` using EF Core.
   - EF entities (new tables; do not reuse old identity tables):
     - `PeerIdentityDbo` (table `PeerIdentities`):
       - Columns: `PeerId (PK, Guid)`, `Name (nvarchar, unique)`, `Version (int, row version)`, `CreatedAtUtc`, `UpdatedAtUtc`.
       - Indexes: `IX_PeerIdentities_Name` unique.
     - `PeerIdentityKeyDbo_V2` (table `PeerIdentityKeys_V2`):
       - Invariant: only one key active at a given time; enforce in app + useful index `(PeerId, NotBeforeUtc)`, `(PeerId, ExpiresAtUtc)`.
       - Indexes: `IX_PeerIdentityKeys_V2_Fingerprint` unique; `IX_PeerIdentityKeys_V2_PeerId`.
     - `PeerVerificationDbo` (table `PeerVerifications`):
       - Columns: `Id (PK)`, `PeerId (FK)`, `Fingerprint (blob)`, `Method (int)`, `VerifiedAtUtc`, `VerifiedBy (nvarchar)`, `Note (nvarchar, nullable)`, `NotBeforeUtc (nullable)`, `ExpiresAtUtc (nullable)`.
       - Indexes: `(PeerId, Fingerprint)`, `(Fingerprint)`.
   - `PercolatorDbContext` schema (fresh):
     - Define `DbSet<PeerIdentityDbo> PeerIdentities` (authoritative peer catalog from day 1).
     - Define `DbSet<PeerIdentityKeyDbo_V2>` and `DbSet<PeerVerificationDbo>` with indexes.
     - All tables that previously referenced `Peers(Id)` will reference `PeerIdentities(PeerId)` from the start (e.g., `PeerConnection.PeerId`, `GrpcEndPoints.PeerId`, `TlsCertificates.PeerId`, `MessageQueue.RecipientPeerId`).
   - Migration plan:
     - Initial migration creates PeerIdentities, PeerIdentityKeys_V2, PeerVerifications, and sets FKs for dependent tables to `PeerIdentities(PeerId)`.
   - Optimistic concurrency:
     - Map `PeerIdentity.Version` to an int column. On `SaveAsync`, compare and increment; throw on mismatch.
   - Lookups required by app:
     - `FindByPublicKeyHashAsync(byte[] fingerprint)` backed by `PeerIdentityKeys_V2.Fingerprint` unique index.
     - `GetByNameAsync(DisplayName)` backed by `PeerIdentities.Name` unique index.
     - Introduce `IPeerIdentityRepository` in application layer and refactor consumers to depend on it.
     - Provide a temporary shim adapter `PeerRepositoryShim : IPeerRepository` that queries the new tables to satisfy legacy code paths while refactors proceed.
     - Gate handler cutover with a feature flag: new handlers use `IPeerIdentityRepository`; old paths continue until removed.
     - Remove the shim and legacy tables once all usages of `IPeerRepository` are eliminated.
   - Deletion checkpoints (do not delete until conditions are met):
     - InMemory repo: Delete `Percolator.Identity/InMemoryPeerIdentityRepository.cs` AFTER Sqlite repo is wired in DI, and all unit tests/integration tests run green against the EF repo or appropriate mocks.
     - Legacy repository: Delete `Percolator.Identity/IPeerRepository.cs` and its implementations AFTER all consumers are migrated to `IPeerIdentityRepository` and the shim is removed.
4) Infrastructure mapping and migration.
   - Map DBOs <-> aggregate; No migration/backfill for existing peers;
   - Implement as adapter/read model sourced from `PeerIdentity` (hash/SPKI lookups), fed by domain events.

5) Cleanup: remove legacy repository and tables via migration (no data preservation needed).
   - Drop tables: Create a migration that DROPs `Peers`, `PeerIdentityKeys` (legacy), and `PeerPublicSigningKeys` (if not used).
   - Ensure all FKs in dependent tables already reference `PeerIdentities(PeerId)` from initial schema.
   - Remove repository: Delete `Percolator.Identity/IPeerRepository.cs` and all implementations in infra once the Sqlite `IPeerIdentityRepository` is wired and all consumers migrated.

6) Refactor app handlers to depend on aggregate behavior, not DBOs.
   - `InitiateHandshakeViaHostHandler`, `HandleHandshakeInitiatorHelloHandler`, and related identity updates.
7) Tests: domain + application.
   - Domain tests for key rotation, single active key, revoke/verify invariants.
   - App tests for observable behavior with repository mocked by aggregate, not DBO shape.
   - update docs and examples to new repository API.


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
- Backpressure: define max in flight per peer; when exceeded, prefer enqueue to host relay with explicit SendResult indicating backpressure.

### Reverse Signal Example
This project already implements the Signal protocol model - peer A aquires a prekey bundle for peer B. Peer A (known as the x3dh inviter) performs the x3dh handshake using the prekey bundle and sends keys to peer B (possibly relayed through an intermediary). Peer B (known as the x3dh responder) then completes the handshake with peer A's keys and responds with a ratchet message containing the shared session id. 

However, this project also already implements a reverse Signal model where peer A (known as the reverse-signal requestor) sends their prekey bundle to Peer B (possibly relayed through an intermediary) as a request. Peer B (known as the x3dh initiator, and the reverse-signal acceptor) then initiates the handshake with peer A's keys and responds to the request with a custom message containing the handshake keys and a ratchet message containing the shared session id. Alice (known as the x3dh responder) then completes the handshake with peer B's keys. 

---

## Cleanup Plan (Cutover Finalization)

Goal: Remove legacy `IPeerRepository` and legacy tables; eliminate transitional backfill; ensure all FKs point to `PeerIdentities`.

### A) Prereqs (status)
- All Application consumers use `IPeerIdentityRepository` (handlers refactored). [Done]
- Transitional adapter for tests present: `Percolator.Application/Identity/PeerIdentityRepositoryAdapter.cs`. [Present]
- Transitional backfill exists in `EstablishDirectSessionHandler.cs` to satisfy FK from `PeerConnections` → `Peers`. [Present]

### B) Migration: Repoint FKs to PeerIdentities and drop legacy tables
1. Update EF model if needed so `PeerConnections.PeerId` FK targets `PeerIdentities(PeerId)`.
   - Verify `Percolator.Infrastructure/Persistence/PercolatorDbContext.cs` sets FK for `PeerConnectionDbo.PeerId` to `PeerIdentities`.
   - If currently mapped to `Peers`, change the relationship to the `PeerIdentities` entity.
2. Add EF Core migration (name: `MovePeerConnectionsToPeerIdentities`).
   - In `Up`:
     - Drop existing FK constraint from `PeerConnections.PeerId` to `Peers.Id`.
     - Create FK from `PeerConnections.PeerId` to `PeerIdentities.PeerId` (cascade: NoAction).
   - In `Down`:
     - Recreate FK to `Peers` (for rollback symmetry).
3. Verify and repoint any other tables still referencing `Peers`:
   - `GrpcEndPoints.PeerId`, `TlsCertificates.PeerId`, `DirectSessions.PeerId` (if any).
   - Ensure they point to `PeerIdentities.PeerId`.
4. Add cleanup migration (name: `DropLegacyPeerTables`).
   - Drop tables: `Peers`, legacy `PeerIdentityKeys` (obsolete), `PeerPublicSigningKeys` (if not used anymore).

### C) Code cleanup (after migrations applied and tests green)
1. Remove transitional backfill in `Percolator.Application/Network/EstablishDirectSessionHandler.cs`:
   - Delete `_legacyPeerRepository` field/ctor param and the `AddOrUpdateAsync(legacyPeer)` call.
2. Remove `PeerIdentityRepositoryAdapter` and usages in tests; mock `IPeerIdentityRepository` directly.
3. Remove `IPeerRepository` and its implementations, and DI registrations (including `PeerRepositoryShim`).
4. Search and delete any remaining references to `Peers` DBOs.

### D) Verification steps
- Build solution and run:
  - `dotnet test ./Percolator.ApplicationTests -v minimal`
  - `dotnet test ./Percolator.ApplicationIntegrationTests -v minimal`
- Smoke run for DHT ping/probe and handshake flows.

### E) Rollback considerations
- Keep `Down` methods in migrations to restore FKs to `Peers` and recreate the dropped tables if needed (dev-only safety).

### F) Notes
- Database is assumed fresh; no data migration required.
- The FK change makes the backfill unnecessary; remove it immediately after the migration lands and tests pass.