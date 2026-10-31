# Percolator.Infrastructure

`Percolator.Infrastructure` provides concrete technical adapters, persistent storage repositories, network transport clients/servers, cryptographic primitives, and binary wire serialization for the Percolator application.

Targeting cross-platform `.NET 10` (`net10.0`), this library implements the outbound ports defined by `Percolator.Domain` and `Percolator.Application` without coupling the core layers to external frameworks or database engines.

For formal layer boundaries, two-stage DI container orchestration, vault header layouts, and technical design choices, see the project [Design Document](Design.md).

---

## Key Subsystems

### 1. Database Vault & Plausible Deniability (`Vault/`)
- **Key Derivation**: Argon2id derivation (64 MB memory, 3 iterations, 4 lanes).
- **Constant-Size Twin-Slot Header**: Stores a master slot and a 76-byte duress slot. When duress is unconfigured, the slot is filled with CSPRNG noise to maintain identical file size (~140 bytes) and entropy.
- **Crypto-Shredder**: 3-pass DoD cryptographic wiping (`CryptoShredder`) executed upon entering a duress passphrase, followed by seamless transition to a clean decoy database.
- **Pinned Master Key Handle**: Pinned unmanaged memory (`IMasterKeyHandle`) with zeroization on disposal (`CryptographicOperations.ZeroMemory`).

### 2. Relational Persistence & CQRS Repositories (`Persistence/`, `Slices/`)
- Encrypted SQLite via SQLCipher (`bundle_e_sqlcipher`) with native C-API keying (`sqlite3_key`).
- Vertical feature slices co-locating EF Core entity mappings, write repositories, and fast-path read query services.
- Reconstitutes domain aggregates via `internal` hydration constructors without exposing public setters.
- Persists all `[GuidId]` identifiers as 16-byte big-endian `BLOB` columns for optimal B-Tree index performance.

### 3. Wire Protocols & Transport (`Protos/`, `Transport/`, `Serialization/`)
- Protobuf schema compilation for internal wire envelopes and control payloads.
- Stepped bucket padding engine to resist traffic analysis.
- gRPC streaming transport adapters and TLS certificate validation callbacks.

### 4. Scheduled Maintenance Workers (`Workers/`)
- Background hosted services for expiring disappearing messages, purging completed outbox jobs, and pruning stale pre-keys.

---

## Testing

Unit tests for infrastructure adapters are co-located in `Percolator.Infrastructure.Tests`:

```bash
dotnet test Percolator.Infrastructure.Tests/Percolator.Infrastructure.Tests.csproj
```
