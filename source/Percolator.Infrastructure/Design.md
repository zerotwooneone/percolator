# Percolator.Infrastructure

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Infrastructure` is the portable, cross-platform (`net10.0`) technical adapter deployable. It realizes the outbound persistence, cryptographic math, wire serialization, and network transport ports defined by the inner Domain and Application layers without introducing operating-system-specific P/Invoke locks.

- **In Scope:** Pre-unlock database vault & plausible-deniability header management, SQLCipher relational persistence & CQRS query projections, concrete cryptographic/ZK math engines, Protobuf wire serialization, gRPC/TLS streaming adapters, portable file storage, and background maintenance workers.
- **Out of Scope:** Windows-specific kernel/CNG APIs (segregated into `Percolator.Infrastructure.Windows`), domain business rules, and UI reactive state.

---

## 2. Architectural Invariants and Encapsulation

1. **Two-Stage DI Host Isolation (`IStage2HostFactory`):** The pre-unlock bootstrap container registers only Stage 1 vault unlock services. `PercolatorDbContext`, domain repositories, query services, and background workers exist exclusively inside the child `IStage2Host` created *after* the database key is verified and schema migrations succeed.
2. **Internal-Only Domain Reconstitution & Protobuf Isolation:** Persistence adapters reconstitute domain aggregates via the `internal` hydration factories defined in [`Percolator.Domain`](../Percolator.Domain/Design.md) and [`Percolator.Apps.FileTransfer.Domain`](../Percolator.Apps.FileTransfer.Domain/Design.md). Compiled Protobuf classes and EF Core entities remain `internal` to infrastructure and never leak across port interfaces.
3. **Vertical Persistence Slices:** Each feature slice co-locates its EF Core entity mapping, aggregate write repository, and zero-hydration SQL read-model query service.

---

## 3. Key Technical Design Choices

1. **Zero OS Keyring Persistence & Pinned Master Key (`IMasterKeyHandle`) + Native `sqlite3_key`:**
   - *Design Choice:* The SQLCipher master key is never stored in OS credential vaults (DPAPI/Keychain) or passed in connection strings. It is unwrapped in memory from `percolator.header` during Stage 1 unlock, held in a pinned byte array (`GC.AllocateArray<byte>(..., pinned: true)`), applied to SQLite connections via native C-API `SQLitePCL.raw.sqlite3_key` inside an EF Core connection interceptor, and cryptographically zeroized upon session lock or disposal.
   - *Rejected Alternative:* Passing hex keys via connection strings (which leaves uncollectable immutable `string` copies on the managed heap) or persisting keys in OS keyrings.
2. **Constant-Size Twin-Slot Header for Plausible Deniability:**
   - The vault header always serializes both a master key slot and a duress slot; when no duress passphrase is set, the duress slot is populated with CSPRNG noise so file size and entropy are indistinguishable from an active duress configuration. Entering the duress passphrase cryptographically shreds the vault before initializing a clean decoy database.
3. **RFC 4122 Big-Endian `BLOB(16)` UUID Storage:**
   - All `[GuidId]` primary and foreign keys are stored as 16-byte big-endian `BLOB` columns (`Guid.ToByteArray(bigEndian: true)`), cutting index footprint in half compared to text UUIDs while preserving lexicographical B-Tree sort order for sequential UUIDv7 keys (per [`Percolator.SourceGenerators`](../Percolator.SourceGenerators/Design.md)).
