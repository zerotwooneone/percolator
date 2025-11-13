# Network Domain: DDD Model and Repository Plan

## Goals and Boundaries
- Build a pure Network domain (no dependencies on other domains like Identity, Chat, DHT, Crypto, Application).
- Replace anemic repositories with rich Aggregates and Value Objects, plus repository interfaces defined in this project.
- Preserve current observable behavior where it belongs in the domain, moving appropriate complexity out of `Percolator.Application.Network/` handlers.
- Infrastructure mappings (EF/Core/Sqlite, gRPC clients, timers) live outside this project.

### TLS Policy
- Transport must use TLS by default. This is a peer-to-peer application with a Trust-On-First-Use (TOFU) model for peer certificates.
- The Network domain treats certificate material opaquely; it records presence/freshness and applies routing eligibility rules.
- A development override MAY disable TLS requirement for local/dev use; production assumes TLS enforced.

## Current State (Survey)
- Key types in `Percolator.Network/`:
  - `PeerId`, `DirectMessagePublicKey`, `PublicKeyHash`, `GrpcEndPoint`, `TlsCertificate`, `Signature`.
  - Aggregates/entities: `PeerConnection`, `DirectSession`, `DiscoveredPeer`.
  - Repos/ports: `IPeerConnectionRepository`, `IDirectSessionRepository`, `ITrustedPeerStore`, discovery ports (`IPeerDiscoveryService`, `IPeerDiscoveryHandler`, `IPeerDiscoveryConfig`).
  - Services: `PeerDiscoveryService` (imperative service; some logic better as domain rules/services).
- Application layer consumers in `Percolator.Application/Network/` to learn desired behavior and invariants:
  - `ProcessInternalEnvelopeHandler.cs` and `DeliverOpaqueMessageHandler.cs`: message flow, session lookup, route decisions, retries.
  - `EstablishDirectSessionHandler.cs` and `GrpcSessionService.cs`: session establishment, endpoint updates, last-seen, certificate handling.
  - `RelayOrchestrator.cs`, `RemoteEnvelopeSender.cs`, `NetworkTransportPortAdapter.cs`: transport orchestration, relay logic, backpressure concerns.

## Ubiquitous Language
- Peer: a remote device we can deliver messages to via one or more endpoints.
- Peer Connection: the authoritative record of how to reach a Peer, its endpoints, security material (TLS), and liveness.
- Direct Session: an established secure channel with session identifier; lifecycle events: established, advanced, expired.
- Endpoint: gRPC address + metadata; has freshness and last-seen timestamps.
- Relay Route: a path through an intermediary peer (optional) for delivery.

## Proposed Aggregates and Value Objects

### Aggregate: PeerRoutingProfile
- Identity: `PeerId` (opaque Guid supplied by Identity domain; Network treats it as a pure VO and never inspects cryptographic material).
- State:
  - Endpoints: collection of `GrpcEndPoint` value objects.
  - Certificates: collection of `TlsCertificate` value objects.
  - Network keys: `DirectMessagePublicKey` (for routing/authorization).
  - Liveness: `LastSeenUtc`, `Reachability` (VO).
  - Relays: zero-to-many `RelayLink` value objects (each links a `PeerId` relay with freshness metadata).
- Invariants/Rules:
  - Endpoints are unique by host:port; updates only mutate freshness timestamps.
  - Certificate chain must be non-empty if TLS required; rotation preserves continuity window.
  - Updating last-seen must monotonically increase per endpoint.
  - Exactly one `PeerRoutingProfile` exists per `PeerId`; binding the same `PeerId` twice must merge/ignore duplicates.
  - Relays are unique per `RelayPeerId`; their freshness decays over time and is refreshed on successful relay observations.
- Behavior (methods):
  - `AddGrpcEndPoint(GrpcEndPoint ep, DateTimeOffset now)`
  - `UpdateLastSeen(GrpcEndPoint ep, DateTimeOffset now)`
  - `RotateCertificates(IEnumerable<TlsCertificate> certs, DateTimeOffset now)`
  - `AddOrRefreshRelay(PeerId relayPeerId, DateTimeOffset now)`
  - `RemoveRelay(PeerId relayPeerId)` / `PruneStaleRelays(DateTimeOffset cutoff)`
  - `RecordReachability(Reachability status, DateTimeOffset now)`
  - `MergeDiscovered(DiscoveredPeer provisional)` (merge endpoints/certs/liveness gathered pre-binding)
  - `BindIdentity(PeerId id)` (idempotent; no-op if already bound; enforces single-routing-profile-per-peer)
- Domain Events:
  - `PeerRoutingProfileEstablished`, `EndpointAdded`, `EndpointRefreshed`, `CertificatesRotated`, `RelayAdded`, `RelayRefreshed`, `RelayRemoved`, `PeerReachabilityChanged`.


### Entity: DiscoveredPeer
- Identity: `DiscoveryKey` (VO) — a provisional identifier until Identity confirms a `PeerId`.
- State: `PublicKeyHash` (when available), candidate `GrpcEndPoint`s, first/last seen, `Confidence` (VO), `Source` (VO), backoff metadata.
- Behavior:
  - `RecordDiscovery(DiscoverySource src, DateTimeOffset now)`
  - `ObserveEndpoint(GrpcEndPoint ep, DateTimeOffset now)`
  - `PromoteToRoutingProfile(PeerId id)` (returns a new/updated `PeerRoutingProfile` and marks this instance as promoted)

### Value Objects
- `Reachability`: Unknown/Online/Offline/Degraded with timestamps.
- `EndpointFreshness`: timestamps and decay policy for `GrpcEndPoint` ranking.
- `RelayLink`: `{ RelayPeerId, Freshness }` using the same freshness concept to rank/select relays.
- `DiscoverySource`: enum VO (SelfReported, DHT, Manual, Cache).
- `Confidence`: scalar with clamp/decay behavior.
- `RelayRoute`: chain constraint rules (only 1 hop allowed, etc.).
- `DiscoveryKey`: opaque provisional key (e.g., `PublicKeyHash` if present; otherwise deterministic `EndpointKey(host:port)` with salt). Network never assumes cryptographic identity from it.
- `CertificatePresence`: transport signal describing certificate availability/age: `Present`, `RequiredMissing`, `ExpiringSoon`.
- `DeliveryOutcome`: `Succeeded` or `Failed(DeliveryFailureReason)`; drives reachability transitions.
- `DeliveryFailureReason`: `Timeout`, `Unavailable`, `RateLimited`, `Unauthenticated`, `PermissionDenied`, `Cancelled`, `Backpressure`, `Unknown`.
- `BackoffState`: attempts counter, lastFailureAt, nextEligibleAt; informs selection/backoff.
- `RetryBudget`: limits on attempts per time window.
- `SelectionTrace` (optional): diagnostic details for route selection (candidates, ranks, exclusions, chosen result).

### Domain Services (pure)
- `RoutePlanner`:
  - Inputs: `PeerRoutingProfile` + `RelayPolicy` + `FreshnessPolicy` + `ReachabilityPolicy` + optional `BackoffState`/`RetryBudget`.
  - Output: preferred `GrpcEndPoint` and optional `RelayLink` given endpoint/relay freshness, reachability, TLS eligibility (CertificatePresence), and policies.
- `FreshnessPolicy`: parameters for decay, thresholds, pruning windows.
- `ReachabilityPolicy`: state transition rules based on `DeliveryOutcome` signals.
- `RelayPolicy`: constraints like single-hop, candidate caps, freshness cutoffs.

## Repository Interfaces (Domain-Owned Ports)
- `IPeerRoutingProfileRepository` (replace current with rich aggregate methods):
  - `Task<PeerRoutingProfile?> GetByIdAsync(PeerId id)`
  - `Task UpsertAsync(PeerRoutingProfile aggregate)`
  - `Task<PeerRoutingProfile?> GetByPublicKeyAsync(IdentityPublicKey pk)`
  - `Task<IEnumerable<PeerRoutingProfile>> GetStaleAsync(DateTimeOffset threshold)`
- `IDiscoveredPeerRepository` (new):
  - `Task<DiscoveredPeer?> GetByDiscoveryKeyAsync(DiscoveryKey key)`
  - `Task<DiscoveredPeer?> GetByPublicKeyHashAsync(PublicKeyHash pkh)`
  - `Task UpsertAsync(DiscoveredPeer entity)`
  - `Task<IEnumerable<DiscoveredPeer>> GetCandidatesAsync(DateTimeOffset seenSince)`
  - `Task<PeerRoutingProfile> PromoteToRoutingProfileAsync(DiscoveredPeer provisional, PeerId id)`
  - `Task<PeerRoutingProfile> BindIdentityAsync(PeerRoutingProfile aggregate, PeerId id)`
- `ITrustedPeerStore` remains as a small domain port (or folded into connection repo if appropriate).

Note: These are domain ports; infrastructure repos (EF/Sqlite, caching) will live in Infrastructure and adapt to these interfaces.

## Anti-Corruption Layer (ACL) Considerations
- Application services such as `RemoteEnvelopeSender`, `RelayOrchestrator`, and handlers should consume only domain types and ports.

## Provisional Identity and Binding Flow
- Discovery produces `DiscoveredPeer` keyed by `DiscoveryKey`.
- When an out-of-domain process (X3DH/Identity) confirms a peer’s identity, it provides an opaque `PeerId` to Network.
- Network promotes/merges: `DiscoveredPeer.PromoteToRoutingProfile(PeerId)` and/or `PeerRoutingProfile.BindIdentity(PeerId)` then `PeerRoutingProfile.MergeDiscovered(...)`.
- Repository invariants ensure one `PeerRoutingProfile` per `PeerId`; collisions merge provisional data and discard duplicates.

## Phased Implementation Plan 
1) Foundation (types only)
- Create new VOs: `Reachability`, `EndpointFreshness`, `DiscoverySource`, `Confidence`, `RelayRoute`, `DiscoveryKey`.
- Tighten existing VOs (`GrpcEndPoint`, `DirectMessagePublicKey`, `PublicKeyHash`) with validation and equality semantics (if any gaps).

2) Aggregates (domain code first)
- Implement `PeerRoutingProfile` aggregate with rules/methods and events.
- Implement `DiscoveredPeer` entity behavior and the promotion/binding path to `PeerRoutingProfile`.
- Unit test domain behavior (pure tests; no infrastructure shims or in-memory repos).

3) Repository Interfaces and Infrastructure
- Define/enrich ports: `IPeerRoutingProfileRepository`, `IDiscoveredPeerRepository`.
- Implement Infrastructure repositories and DB schema (tables, indexes) for the new domain.

4) Update call sites
- Update `Percolator.Application.Network` handlers/services to use the new domain APIs and repositories.
- Map transport errors to `DeliveryOutcome`; use `RoutePlanner` for selection; record reachability via `PeerRoutingProfile`.
  - Call sites to update:
    - `Percolator.Application/Network/ProcessInternalEnvelopeHandler.cs`
    - `Percolator.Application/Network/DeliverOpaqueMessageHandler.cs`
    - `Percolator.Application/Network/RelayOrchestrator.cs`
    - `Percolator.Application/Network/RemoteEnvelopeSender.cs`
    - `Percolator.Application/Network/GrpcMessageTransportService.cs`

5) Eventing Strategy
- Domain event types in this project (POCOs). Application subscribes to publish/observe effects.

6) Cleanup
- Remove legacy code and legacy tables (see Cleanup sections below). No data migration; use a new SQLite DB file.

## Out-of-Scope (Removed from Network Domain)
- DirectSession aggregate and crypto session lifecycle are intentionally excluded from the Network domain plan.
  - Rationale: session establishment (X3DH), Double Ratchet state, key rotation/expiration windows, header/message key counters, and signature verification are cryptographic concerns, not transport topology.
  - Likely Destination: a dedicated Crypto/Sessions domain. Network will react to session events, but will not own session state.

- What is being “lost” from Network by removing DirectSession:
  - Crypto session state: ratchet keys, secrets, counters, transcript hashes.
  - Handshake artifacts and validation rules (X3DH, signature verification).
  - Crypto-driven expiration/rotation policies.
  - Authorization/trust decisions bound to identity key material.

- How Network compensates:
  - Use `PeerRoutingProfile.RecordReachability(...)` and freshness policies to reflect transport liveness (derived from success/failure signals emitted by higher layers).
  - `RoutePlanner` selects endpoints/relays based on endpoint and relay freshness; it does not depend on crypto session internals.
  - Listen to external session events (e.g., SessionEstablished, SessionActivity, SessionExpired) to optionally update reachability, but do not persist session state.

## Non-Functional Requirements Captured in Domain
- Deterministic endpoint selection given equal freshness (stable ordering rules).
- Monotonic timestamps to prevent clock skew anomalies in liveness.

## Open Questions
- Relay policy constraints: single hop only? Criteria for selecting relay?
- Certificate rotation overlap rules (grace period length?)
- How to model backpressure and delivery attempts: belong to Network or MessageQueue domain?

## Deliverables (Artifacts in this project)
- Aggregates: `PeerRoutingProfile`.
- Entities: `DiscoveredPeer`.
- Value Objects: `Reachability`, `EndpointFreshness`, `RelayLink`, `DiscoverySource`, `Confidence`, `RelayRoute`, `DiscoveryKey`.
- Domain Ports: `IPeerRoutingProfileRepository` (enriched), `IDiscoveredPeerRepository` (new), `ITrustedPeerStore` (confirm placement).
- Domain Services: `RoutePlanner`.

## Post-Definition Cleanup Plan
- After defining and implementing the new Network domain objects and their persistence (Infrastructure mappings/tables), remove legacy artifacts:
  - Code: delete obsolete anemic repositories, DTO/DBO types, and unused services/adapters from the old Network layer.
  - Schema: add EF migrations to DROP legacy Network tables superseded by the new schema (e.g., previous PeerConnections-related tables, relay link tables), plus indexes/constraints tied to them.
  - Config: remove DI registrations for the old repositories/services.
- No data migration required; we will use a new SQLite DB file for cutover.
- Verification: run full unit/integration tests; smoke test routing (direct and via relay) with TLS enabled by default (TOFU) and with the dev override.

### Rolling Cleanup Notes (to be updated as we refactor)
- Track specific code/files to delete as each new capability lands (append to the legacy list above):
  - Code: files replaced by `PeerRoutingProfile`, `DiscoveredPeer`, and `RoutePlanner`.
  - Schema: columns/tables superseded by new tables (e.g., legacy `PeerConnections`, `DirectSessions`, `RelayPeerId` columns).
  - DI: registrations for legacy repositories/services.

## Execution Plan (AI-sized TDD steps)
- No shims or in-memory repositories. Each step follows Red-Green-Refactor with explicit build/test gates.

### Step 1: Core Value Objects (batch A)
- Scope: `EndpointFreshness`, `Reachability`, `RelayLink`, `RelayPolicy`, `FreshnessPolicy`, `ReachabilityPolicy`.
- Red
  - Generate VO stubs and unit tests covering equality, validation, decay/threshold rules, and basic state transitions.
  - Build solution; fix compiler errors only.
  - Run tests; confirm failures (red) due to stubs.
- Green
  - Implement minimal behavior to satisfy tests.
  - Build; run tests; ensure all pass (green).
- Refactor
  - Improve clarity and remove duplication.
  - Build; run tests.

### Step 2: Delivery/Backoff Signals (batch B)
- Scope: `DeliveryOutcome`, `DeliveryFailureReason`, `BackoffState`, `RetryBudget`, `CertificatePresence`, optional `SelectionTrace`.
- Red: stubs + tests (mapping and invariants) → build → run tests (red).
- Green: implement minimal behavior → build + run tests (green).
- Refactor: tidy → build + run tests.

### Step 3: DiscoveredPeer (provisional identity)
- Scope: entity with `RecordDiscovery`, `ObserveEndpoint`, `PromoteToRoutingProfile(PeerId)`.
- Red: entity stub + tests for discovery accumulation, confidence updates, and promotion contract → build → run tests (red).
- Green: implement behavior → build + run tests (green).
- Refactor: tidy invariants/logical duplication → build + run tests.

### Step 4: PeerRoutingProfile (basics)
- Scope: `AddGrpcEndPoint`, `UpdateLastSeen`, `RotateCertificates`, `BindIdentity`, `MergeDiscovered`, `RecordReachability` (direct outcomes only).
- Red: aggregate stub + tests for rules/invariants/events → build → run tests (red).
- Green: implement behavior → build + run tests (green).
- Refactor: tidy → build + run tests.

### Step 5: PeerRoutingProfile (relays and pruning)
- Scope: `AddOrRefreshRelay`, `RemoveRelay`, `PruneStaleRelays`; relay freshness interplay with policies.
- Red → build → run tests (red).
- Green → build + run tests (green).
- Refactor → build + run tests.

### Step 6: RoutePlanner service
- Scope: deterministic selection using endpoint/relay freshness, reachability, certificate presence, and policies; emits optional `SelectionTrace`.
- Red → build → run tests (red).
- Green → build + run tests (green).
- Refactor → build + run tests.

### Step 7: Repository ports and Infrastructure tables
- Scope: define ports `IPeerRoutingProfileRepository`, `IDiscoveredPeerRepository` (domain); implement EF/Sqlite repositories and schema in Infrastructure.
- Red
  - Add interfaces and infra stubs with compile-only tests ensuring ports are referenced (no runtime DB needed yet) → build → run tests (expected red if behavior tests assert persistence).
- Green
  - Implement EF models/mappings/tables for new domain; implement repository methods; add basic repository tests against a temp DB file.
  - Build + run tests (green).
  - Refactor: indexes/constraints and code cleanup → build + run tests.

### Step 7a: Entity and Persistence Modeling (Authoritative)
- Objective: design the best-fit entity models and persistence schema without being constrained by legacy data. No migration required; we will create new tables optimized for the domain.

- Entities and Value Objects (authoritative definition)
  - PeerRoutingProfile (Aggregate)
    - Identity: `PeerId`
    - Fields
      - `IdentityPublicKey` (bytes; nullable until known)
      - `ReachabilityStatus` (enum int), `ReachabilityLastChangeUtc` (utc)
      - Endpoints: many `GrpcEndPoint { Host, Port, LastSeenUtc, FirstSeenUtc }`
      - Relays: many `RelayLink { RelayPeerId, LastSeenUtc }`
      - Certificates: many `TlsCertificate { RawData, RawDataHash, AddedAtUtc }`
    - Invariants
      - Unique by `PeerId`
      - Endpoints unique per `(PeerId, Host, Port)`
      - Relays unique per `(PeerId, RelayPeerId)`
      - Certificates de-duplicated by `RawDataHash`
    - Queries
      - `GetByIdAsync(PeerId)`
      - `GetByPublicKeyAsync(IdentityPublicKey)`
      - `GetStaleAsync(threshold)` based on `ReachabilityLastChangeUtc` and/or endpoint freshness

  - DiscoveredPeer (Entity)
    - Identity: `DiscoveryKey` (VO)
    - Fields
      - `PublicKeyHash` (nullable until available)
      - `FirstSeenUtc`, `LastSeenUtc`
      - `Source` (DiscoverySource), `Confidence` (double/VO)
      - `PromotedAtUtc` (nullable), `BoundPeerId` (nullable)
      - Observed endpoints: many `DiscoveredPeerEndpoint { Host, Port, FirstSeenUtc, LastSeenUtc }`
      - Backoff metadata (optional future): attempts, lastFailureAt, nextEligibleAt
    - Invariants
      - Unique by `DiscoveryKey`
      - Endpoints unique per `(DiscoveryKey, Host, Port)`
      - When `BoundPeerId` is set, promotion is irreversible; `PromotedAtUtc` must be populated
    - Queries
      - `GetByDiscoveryKeyAsync(DiscoveryKey)`
      - `GetByPublicKeyHashAsync(PublicKeyHash)`
      - `GetCandidatesAsync(seenSince)` ordered by recency/confidence
      - `PromoteToRoutingProfileAsync` creates/merges into `PeerRoutingProfile`, sets `BoundPeerId`, `PromotedAtUtc`

- Database schema (proposed)
  - Table: `PeerRoutingProfiles`
    - PK: `PeerId` (GUID)
    - Columns: `IdentityPublicKey` (BLOB, null), `ReachabilityStatus` (INT, not null), `ReachabilityLastChangeUtc` (DATETIME, null)
    - Indexes: `IX_PeerRoutingProfiles_IdentityPublicKey` (non-unique) for reverse lookup
  - Table: `PeerRoutingEndpoints`
    - PK: `Id` (INTEGER)
    - Columns: `PeerId` (GUID, FK→PeerRoutingProfiles), `Host` (TEXT), `Port` (INT), `FirstSeenUtc` (DATETIME), `LastSeenUtc` (DATETIME)
    - Unique: `(PeerId, Host, Port)`
    - On delete: cascade
  - Table: `PeerRoutingRelays`
    - PK: `Id` (INTEGER)
    - Columns: `PeerId` (GUID, FK), `RelayPeerId` (GUID), `LastSeenUtc` (DATETIME)
    - Unique: `(PeerId, RelayPeerId)`
    - On delete: cascade
  - Table: `PeerRoutingCertificates`
    - PK: `Id` (INTEGER)
    - Columns: `PeerId` (GUID, FK), `RawData` (BLOB), `RawDataHash` (BLOB), `AddedAtUtc` (DATETIME)
    - Index: `RawDataHash` (non-unique or unique if desired)
    - On delete: cascade
  - Table: `DiscoveredPeers`
    - PK: `DiscoveryKey` (TEXT)
    - Columns: `PublicKeyHash` (BLOB, null), `FirstSeenUtc` (DATETIME), `LastSeenUtc` (DATETIME), `Source` (INT), `Confidence` (REAL), `BoundPeerId` (GUID, null), `PromotedAtUtc` (DATETIME, null)
    - Indexes: `PublicKeyHash` (non-unique), `BoundPeerId` (non-unique)
  - Table: `DiscoveredPeerEndpoints`
    - PK: `Id` (INTEGER)
    - Columns: `DiscoveryKey` (TEXT, FK→DiscoveredPeers), `Host` (TEXT), `Port` (INT), `FirstSeenUtc` (DATETIME), `LastSeenUtc` (DATETIME)
    - Unique: `(DiscoveryKey, Host, Port)`
    - On delete: cascade

- Repository behaviors
  - Upsert semantics
    - Merge endpoints/relays/certificates by natural keys; update timestamps if newer
    - For DiscoveredPeer, accumulate endpoints and update `LastSeenUtc`; do not drop historical candidates prematurely
  - Promotion
    - `PromoteToRoutingProfileAsync` copies freshest observed endpoints and certificate presence into `PeerRoutingProfile`, sets `BoundPeerId` and `PromotedAtUtc` on `DiscoveredPeer`
  - Stale queries
    - `GetStaleAsync` uses `ReachabilityLastChangeUtc` and endpoint freshness thresholds

- Notes
  - This design intentionally separates provisional observations (DiscoveredPeers) from authoritative routing (PeerRoutingProfile)
  - No data migration: create new tables and cut over; legacy tables can be dropped in Step 9

#### Step 7a Implementation Status
- Implemented (domain):
  - DiscoveredPeer authoritative model (DiscoveryKey identity, endpoint accumulation, promotion)
  - PeerRoutingProfile merge behavior and IdentityPublicKey support
- Implemented (infrastructure):
  - Repositories: SqliteDiscoveredPeerRepository and SqlitePeerRoutingProfileRepository
  - Persistence: DiscoveredPeers, DiscoveredPeerEndpoints, PeerRoutingProfiles, PeerRoutingGrpcEndPoints, PeerRoutingRelays, PeerRoutingTlsCertificates
  - Behaviors: upsert/merge for endpoints, relays; certificate rotation replace+hydrate; stale and candidate queries (client-side filtering where SQLite limits apply)
- Tests: Domain and Infra tests cover promotion, merges, endpoint freshness, relays, certificate rotation, and queries.
- Remaining Nice-to-haves:
  - De-duplicate certificates on RawDataHash at DB-level (unique index optional)
  - Add pruning windows and policies in repositories (configurable thresholds)
  - Add RoutePlanner service tests once Step 6 is executed

### Step 8: Update application call sites
- Scope: switch call sites to new domain APIs (see list in the plan) and map transport errors to `DeliveryOutcome`.
- Red
  - Update usage to new types/methods; adjust tests to assert interactions; build; fix compiler errors.
  - Run tests; confirm red where behavior not yet wired.
- Green
  - Wire integration with repositories and `RoutePlanner`; adapt handlers to record reachability and apply policies.
  - Build + run tests (green).
- Refactor: simplify orchestration; ensure Network domain owns routing rules → build + run tests.

### Step 9: Cleanup (code + schema)
- Scope: remove legacy code and drop legacy tables (per Cleanup sections). No data migration; use a new SQLite DB file.
- Execute removals and create EF migration(s) to drop tables/columns.
- Build; run tests; smoke test with TLS default (TOFU) and dev override.
Legacy items to remove after Network domain cutover
- Code (Network/Application)
  - Percolator.Infrastructure/Network/SqlitePeerConnectionRepository.cs
  - Percolator.Infrastructure/Network/SqliteDirectSessionRepository.cs
  - Percolator.Network/DirectSession.cs
  - Percolator.Network/IDirectSessionRepository.cs
  - Any adapter/shim around direct sessions used in Application (e.g., GrpcSessionService dependencies tied to repo)
  - Legacy reachability/relay logic embedded in Application handlers once superseded by RoutePlanner and PeerRoutingProfile
  - Old discovery glue that duplicates DiscoveredPeer behavior
- Persistence (Infrastructure)
  - DBOs:
    - Percolator.Infrastructure/Persistence/PeerConnectionDbo.cs
    - Percolator.Infrastructure/Persistence/DirectSessionDbo.cs
  - Mapped tables and FKs introduced by older migrations:
    - PeerConnections
    - columns like RelayPeerId on PeerConnections (e.g., migration 20251028061909_AddRelayPeerIdToPeerConnections)
  - DirectSessions and related indexes
    - Any legacy relay tables or join structures replaced by RelayLink in the new schema
    - Older indexes unique to the old model (e.g., on PeerConnections endpoints) that are not part of the new design
- Migrations to consider for drop
  - 20251028061909_AddRelayPeerIdToPeerConnections
  - 20251110044916_MovePeerConnectionsToPeerIdentities (ensure replaced by new FK layout under the new schema)
  - Any migration paths that only exist to maintain the legacy PeerConnections/DirectSessions model
- DI registrations
  - Remove old repo registrations for IPeerConnectionRepository and IDirectSessionRepository when the new ports land:
  - Replace with IPeerRoutingProfileRepository and IDiscoveredPeerRepository registrations
  - Remove any transient services that only existed to bridge legacy concepts
