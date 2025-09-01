# Plan: Scripted DHT Probe (Host, Alice, Bob) + CLI and Store Separation

Goal: Update the PowerShell scripts and CLI to run a host plus two clients (Alice, Bob) and exercise DHT probe end-to-end with isolated identities and storage. Remove reliance on the old test harness; keep the important lessons learned below.

## Scope

- Update scripts: `test-dht-probe-with-log.ps1` and `test-dht-probe.ps1` to orchestrate three processes: Host, Alice, Bob.
- Add a new CLI command in `Percolator.Node/Program.cs` to create a self identity (`CreateSelfIdentityCommand`).
- Normalize a `--selfIdentity` option across `host` and `send` commands (and any DHT-related command), and make them use the new CLI command to ensure identity exists (idempotent: succeeds if identity already exists).
- Separate `IDoubleRatchetSessionStore` by `SelfIdentityId` (like `IDirectSessionRepository`), with required calling code and test updates.

## Steps

1) CLI: CreateSelfIdentity command
   - Implement a `create-identity` CLI command in `Percolator.Node/Program.cs`.
   - Command executes `IMediator.Send(new CreateSelfIdentityCommand(name, null))`.
   - If identity exists, command should exit successfully (no-op) to support idempotent scripts.

2) Normalize selfIdentity option
   - Add `--selfIdentity <name>` to:
     - `host` command
     - `send` command
     - Any DHT probe command entry points (if exposed via CLI; otherwise piggyback on `send` or a new `dht-probe` command).
   - Ensure each command resolves/loads the specified identity before executing.

3) Session store separation by SelfIdentityId
   - Update `IDoubleRatchetSessionStore` to include `selfIdentityId` on all persistence operations (mirroring `IDirectSessionRepository`).
   - Sub-steps:
     - Update interface(s) in `Percolator.Cryptography` (or relevant project) to include `SelfIdentityId` parameter.
     - Update implementations in `Percolator.Infrastructure/Cryptography` to partition data per `SelfIdentityId` (DB schema changes if needed: either composite keys or a SelfIdentityId column + composite unique indexes).
     - Update calling code in `Percolator.Application` where the store is used to pass the active `SelfIdentityId` from `ActiveIdentityContext`.
     - Update tests in `Percolator.InfrastructureTests` and `Percolator.CryptographyTests` to create/use explicit self identities and assert isolation.
     - EF Core migration(s) required in `Percolator.Infrastructure` to evolve the SQLite schema for DoubleRatchet tables. Proposed migration name: `AddSelfIdentityPartitionToDoubleRatchet`.
     - Runtime gating: application must fail fast if pending migrations exist (no auto-migrate in production run paths).
     - Data upgrade: not required (we will use a fresh DB file for this change).
     - Environment behavior: In Development, the node will apply EF migrations automatically on startup. In non-Development, it will fail fast if any pending migrations are detected.
   - Partition completeness verification:
     - Review all repositories/stores (e.g., `IDirectSessionRepository`, peer connections, conversations, any caches) to ensure `SelfIdentityId` is included in keys/queries.
     - Suggest fixes only if gaps are found; current implementation is likely sufficient.

4) Scripts: test-dht-probe-with-log.ps1 / test-dht-probe.ps1
   - Create identities (idempotent):
     - `percolator.exe create-identity --name host`
     - `percolator.exe create-identity --name alice`
     - `percolator.exe create-identity --name bob`
   - Start the server (Host only):
     - `percolator.exe host --selfIdentity host`
   - Run client probes (do NOT start host for these):
     - Alice: `percolator.exe dht-probe --selfIdentity alice --target localhost:<port> --name host` and print nearest results (expect none).
     - Bob: `percolator.exe dht-probe --selfIdentity bob --target localhost:<port> --name host` and print nearest results (expect Alice).
   - Storage:
     - A single shared `Storage:Path` is acceptable because data is partitioned by `SelfIdentityId`.
     - No special CLI flags are required for storage or logging.
   - Command availability:
     - Use the existing `dht-probe` CLI command (already implemented).
   - Resilience requirements:
     - Treat `create-identity` as idempotent; continue if identity already exists.
     - No retry logic for probes; assume Host is up before invoking `dht-probe`.
     - Use identity fallback behavior: explicit name first, then fallback to `default`.
   - Host port:
     - Default Host port is 5000; scripts may accept a parameter to override and pass it via `--port`.

5) Storage and isolation
   - Use a shared `Storage:Path` for Host/Alice/Bob; correctness relies on strict partitioning by `SelfIdentityId` across repositories and stores (including the new DoubleRatchet partitioning step).
   - Confirm the process reads `Storage:Path` (see infra’s `ServiceCollectionExtensions`).

6) Validation
   - After Alice’s probe: nearest list is empty.
   - After Bob’s probe: nearest list should include Alice.
   - No duplicate conversations or unique constraint violations across isolated stores.

## Concerns / Clarifications

- EF migrations scope: Confirm migrations live in `Percolator.Infrastructure` and cover all DoubleRatchet tables (sessions, skipped keys, message counters). Migration name proposed: `AddSelfIdentityPartitionToDoubleRatchet`.
- Data upgrade: If existing DBs are present, do we backfill `SelfIdentityId` (e.g., default to the active identity at upgrade) or require a clean DB for now?
- Runtime gating: Should the app refuse to start if the schema is pre-migration (vs. quietly running with EnsureCreated)? Prefer explicit failure to avoid mixed schemas.
- Partition completeness: Besides DoubleRatchet, verify all repos/stores already include `SelfIdentityId` in unique indexes/queries (e.g., `IDirectSessionRepository`, peer connections, conversations) to safely share `Storage:Path`.
- CLI resilience specifics: acceptable retry/backoff for `dht-probe` while waiting for Host? Proposal: retry up to 10 times with 500ms delay.
- Host port/source of truth: Which port should scripts assume for `--target`? Confirm default from `Percolator.Node/Program.cs` or require an explicit port parameter in scripts.
- Identity fallback behavior: Using `default` fallback via `IIdentityOrchestrator.ResolveIdentityAsync(...)`—confirm desired precedence (explicit name first, then fallback) is final.
- CLI surface: if a `dht-probe` command does not exist, we can add one; otherwise reuse existing command that triggers probe via MediatR.
- Scripts must be resilient (retry on port-in-use, ensure clean temp dirs, and not fail when identity already exists).

## New Findings (2025-08-31)

- The duplicate conversation inserts observed during `PingAndFindNode_WithThreeNodes_ShouldDiscoverPeersViaHost` are due to SQLite data leakage between nodes.
- Specifically, the integration test configuration currently sets `Percolator:DataDirectoryPath`, but infrastructure reads `Storage:Path` (see `Percolator.Infrastructure/Identity/ServiceCollectionExtensions.cs` and `Percolator.Infrastructure/ServiceCollectionExtensions.cs`).
- Because `Storage:Path` is unset, it defaults to `%LocalAppData%`, causing Host and Alice to share the same `percolator.db`. As a result:
  - Host inserts a conversation for Alice.
  - Alice, using the same DB file, attempts to insert the same conversation, triggering the unique constraint violation.

# Notes

Architectural decision : `IDirectSessionRepository` is the authoritative mapping between a remote peer and a direct session.

## Rationale

- `IDirectSessionRepository` is the single source of truth that binds a remote `PeerId` to a `DirectSessionId` (the cryptographic session identifier used by the transport and Double Ratchet).
- Previous lookups in `ConversationService.GetExistingDirectConversationAsync()` relied on `IPeerConnectionRepository` + `IConversationRepository` via ChannelId, which could miss existing sessions and cause duplicate inserts.
- Domain code should use `Percolator.Network.DirectSessionId` for direct sessions instead of `Percolator.Chat.ValueObjects.ConversationId`.

