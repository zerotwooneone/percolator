# Self Identity – DDD Integration Plan (High Level)

## Goals
- **Persisted identity on startup**
  - On app start, read the encrypted SQLite store for the most recently used SelfIdentity.
  - If none exists, create a new SelfIdentity and persist it securely.
- **DDD model-first**
  - Replace `Percolator.Application.Identity.SelfIdentityDto` with a domain model aggregate in `Percolator.Identity`.
  - Evolve repository contracts to work with the domain model (not DTOs).
- **Application orchestration**
  - The Application layer coordinates Identity and Sessions (Crypto) without creating cross-domain references.
  - Provide a single startup use case to resolve-or-create an active identity and set it into runtime context.

## Architecture Overview
- **Identity Domain (Percolator.Identity)**
  - Refined `SelfIdentity` aggregate :
    - State:
      - `SelfId Id` (int, DDD value type)
      - `DisplayName? DisplayName`
      - `IReadOnlyList<IdentityKey> Keys` (reuse `IdentityKey`)
      - `DateTimeOffset LastUsedUtc`
    - Behavior:
      - `SetDisplayName(DisplayName|string)`
      - `AddKey(byte[] spki, DateTimeOffset notBefore, DateTimeOffset expiresAt, DateTimeOffset now)`
        - Reject overlapping active key windows at `now` 
      - `IdentityKey? GetActiveKey(DateTimeOffset when)`
      - `IdentityKey? GetNextScheduledKey(DateTimeOffset when)`
      - `TouchLastUsed(DateTimeOffset when)`
  - Domain repository contract (`ISelfIdentityRepository`):
    - `Task<SelfIdentity?> GetMostRecentAsync()`
    - `Task<SelfIdentity?> GetByIdAsync(SelfId id)`
    - `Task<IReadOnlyList<SelfIdentity>> ListAsync()`
    - `Task SaveAsync(SelfIdentity identity)`
    - no CreateAsync - we need a domain factory, not an infra one. domain factory ensures created self identity always has a key
- **Infrastructure (Percolator.Infrastructure)**
  - Implements domain `ISelfIdentityRepository` over encrypted SQLite.
  - Manages schema/migrations, encryption, and secure key storage abstractions.
- **Application (Percolator.Application)**
  - Orchestrates cross-domain workflows; no domain↔domain references.
  - Use cases/services:
    - `ResolveOrCreateSelfIdentityOnStartup`
    - `SetActiveIdentityContext` (uses `IActiveIdentityMutator`)
    - Composition helpers for listing sessions for active identity (via Crypto domain)

## Startup Flow (Desired)
1. App start (WPF) → Application use case `ResolveOrCreateSelfIdentityOnStartup`.
2. If none, create new `SelfIdentity` domain entity and persist; mark MRU.
3. Create identity DI scope → `IActiveIdentityMutator.SetActiveIdentity(...)` with domain-safe key handles.
4. Resolve `SessionShellViewModel` + `SessionsSidebarViewModel` from identity scope → show SessionShell.

## Migration Plan (Incremental)
- Phase 1: Introduce domain `SelfIdentity` and new domain `ISelfIdentityRepository` alongside the existing DTO.

### Phase 1 — Detailed Chunks (cutover without shims)
- **Chunk 1: Add domain model and contracts**
  - Create `Percolator.Identity.Model.SelfIdentity` aggregate 
  - Add domain-facing `Percolator.Identity.ISelfIdentityRepository` with:
    - `GetMostRecentAsync()`, `GetByIdAsync(IdentityId)`, `ListAsync()`, `SaveAsync(SelfIdentity)`, optional `CreateAsync(...)`.
  - Keep existing Application-layer `Percolator.Application.Identity.ISelfIdentityRepository` and `SelfIdentityDto` untouched for the moment.
  - TDD:
    - RED: Identity domain tests for `SelfIdentity` invariants (no overlapping active key at `now`, `TouchLastUsed` updates timestamp, `GetActiveKey` window logic).
    - GREEN: Minimal `SelfIdentity` implementation to satisfy tests.
    - REFACTOR: Consolidate common key-window helpers with `PeerIdentity` if desired (no cross-domain coupling).

- **Chunk 2: Expand DB schema (EF migration)**
  - Update `Percolator.Infrastructure.Persistence.SelfIdentityDbo` to include `LastUsedUtc` (non-nullable, no data backfill) and any required fields to support the domain aggregate.
  - Update `PercolatorDbContext` mappings if necessary; generate EF migration to alter the existing SelfIdentity table.
  - Add an index on `LastUsedUtc` for MRU query.
  - unique constraint on Name
  - Note: Build may be broken between editing DBO/context and adding migration; this is acceptable during the cutover.
  - TDD:
    - RED: Infrastructure test expecting `SelfIdentityDbo` to have `LastUsedUtc` and MRU order via query (integration-style with TestHost/SQLite in-memory or file-based).
    - GREEN: Add field, migration, and index; update mapping until test passes.
    - REFACTOR: Clean up migration naming and ensure backward compatibility for existing rows (nulls allowed).

- **Chunk 3: Implement domain repo over SQLite**
  - Implement `Percolator.Infrastructure.Identity.SqliteSelfIdentityDomainRepository : Percolator.Identity.ISelfIdentityRepository` mapping `SelfIdentityDbo` ↔ `SelfIdentity`.
  - Implement `GetMostRecentAsync` using `ORDER BY LastUsedUtc DESC NULLS LAST` semantics; `ListAsync()` to enumerate; `SaveAsync()` to persist and update `LastUsedUtc`.
  - Register the domain repo in `ServiceCollectionExtensions.AddIdentityInfrastructure` alongside existing application repo registrations (temporarily both exist).
  - TDD:
    - RED: Application/Infrastructure tests asserting repo returns MRU, list order, and round-trip save semantics for `LastUsedUtc` and keys.
    - GREEN: Implement repository and mappings.
    - REFACTOR: Remove duplication in mappers; ensure query performance with index.

- **Chunk 4: Desktop startup service (resolve-or-create in UI layer) and wire WPF**
  - Create `Desktop.Wpf` service (e.g., `StartupIdentityService`) that depends on the domain `ISelfIdentityRepository` and encapsulates startup policy:
    - Load MRU via `GetMostRecentAsync()`; if none, create and save a new domain `SelfIdentity` (generate keys in infra or via injected factory as appropriate), and mark `LastUsedUtc`.
    - Return the selected domain `SelfIdentity` and any key handles/metadata needed for `IActiveIdentityMutator`.
  - Update `ShellViewModel` startup to call this Desktop service directly (Application layer remains the orchestrator between domains but does not own UI startup policy).
  - Continue to wire identity scope + `IActiveIdentityMutator` after resolution, as today.
  - TDD:
    - RED: Desktop.Wpf tests asserting the service returns MRU if present; otherwise creates and saves a new identity and updates `LastUsedUtc`.
    - GREEN: Implement the Desktop startup service using the domain repo; inject into `ShellViewModel` via DI.
    - REFACTOR: Remove ad-hoc identity fetch in `ShellViewModel` once tests pass; keep clear boundaries (UI policy in Desktop, cross-domain orchestration in Application).

- **Chunk 5: Remove DTO repo usages (break CLI/daemon temporarily)**
  - Replace `IdentityOrchestrator.ResolveIdentityAsync` lookup-by-name with domain repo usage:
    - Use `ListAsync()` + filter by display name, or `GetMostRecentAsync()` when an active/default is desired.
  - Update `Percolator.Application.Cli.HostCommandHandler` and `Percolator.Node.Program.cs` callsites accordingly.
  - Delete `SelfIdentityDto` and `Percolator.Application.Identity.ISelfIdentityRepository` after all callsites are migrated.
  - TDD:
    - RED: CLI/daemon tests (or integration tests) asserting identity resolution via new path (list+filter or MRU) and that `ActiveIdentityContext` is set.
    - GREEN: Update `IdentityOrchestrator` and callsites.
    - REFACTOR: Remove DTO-specific code and registrations; keep builds green.

#### Deletions at cutover (after all callsites are migrated)
- In `Percolator.Application/Identity`:
  - `SelfIdentityDto.cs`
  - `ISelfIdentityRepository.cs`
  - Any DTO-centric helper methods in `IdentityOrchestrator` that depend on the above (after refactor complete)
- In `Percolator.Infrastructure/Identity`:
  - `SqliteSelfIdentityRepository.cs` (Application-level DTO repo)
  - Any mapping code or helpers solely for DTOs
- Ensure registrations are removed in `Percolator.Infrastructure.Identity.ServiceCollectionExtensions` for the Application-level repo (keep the new domain repo registration).

### Compatibility notes
- Existing callsites (e.g., `HostCommandHandler`, `Node.Program.cs`) currently use `IdentityOrchestrator` to look up self identity by name. The new domain repo does not expose name-based lookup directly.
- Update those callsites to:
  - Use `ISelfIdentityRepository.ListAsync()` to enumerate self identities
  - Filter by display name in application code to select the intended identity
  - Optionally, if an "active" identity concept is used in CLI/daemon, use `GetMostRecentAsync()`

## Testing Strategy
- Identity domain tests: `SelfIdentity` invariants and repo contract (against in-memory/test SQLite).
- Application tests: `ResolveOrCreateSelfIdentityOnStartup` happy/empty-paths; MRU logic.
- Desktop.Wpf tests: startup composition still shows Loading → SessionShell with domain identity.

## Security Considerations
- Encrypted SQLite: manage encryption keys via a secure provider (DPAPI, OS keystore, or user secret provider).
- Avoid persisting raw private keys directly where possible; prefer protected storage and key handles.
- Threat model for MRU selection and identity enumeration (only reveal the current identity required for UX).
