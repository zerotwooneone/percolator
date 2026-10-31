# Percolator.SourceGenerators

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.SourceGenerators` is the compile-time Roslyn metaprogramming deployable at the root of the build graph. It executes strictly as a compiler analyzer (`PrivateAssets="all"`, `IncludeBuildOutput="false"`) to emit strongly typed value-object boilerplate into consuming assemblies without introducing a runtime binary dependency.

- **In Scope:** Incremental source generation (`IIncrementalGenerator`) and compile-time structural diagnostics for byte-backed value objects (`[ByteArray]`) and GUID-backed identifiers (`[GuidId]`).
- **Out of Scope:** Runtime domain logic, cryptographic operations, or I/O.

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-ZERO-PRIMITIVES` (Zero Implicit or Explicit Primitive Casts):** Generated `[ByteArray]` and `[GuidId]` types omit `implicit` and `explicit` cast operators to or from raw `byte[]`, `ReadOnlySpan<byte>`, or `Guid`. Callers must use explicit named factories (`FromBytes`, `FromSpan`, `FromGuid`, `New`) so boundary validation and unit transitions are visible at every call site.
2. **`INV-ENCAPSULATED-BYTES` (Encapsulated Backing Memory):** `[ByteArray]` types wrap a `private readonly ReadOnlyMemory<byte>` with a `private` constructor, enforcing length constraints on every construction path before an instance can exist.
3. **`INV-SYNTAX-FIRST-FILTER` (Syntax-First Incremental Filtering):** Generators filter candidate syntax nodes via `ForAttributeWithMetadataName` prior to semantic model binding to prevent IDE compilation lag.

---

## 3. Key Technical Design Choices

1. **`DEC-FIXED-TIME-EQUALS` (Constant-Time Equality by Default):**
   - *Design Choice:* `[ByteArray]` value objects frequently hold cryptographic keys, hashes, authentication tags, or blind tokens; generated `Equals` methods always compare spans via `CryptographicOperations.FixedTimeEquals`.
   - *Rejected Alternative:* Standard sequential byte comparison, which exposes timing side-channels if an engineer compares two byte-backed value objects with `==` or `.Equals()`.
2. **`DEC-OWNED-BUFFER-OPTIN` (Defensive Copy vs. Explicit Ownership Transfer):**
   - Standard factories (`FromBytes`) copy the input buffer to guarantee immutability, while `FromBytesOwned` is provided as an explicit opt-in for performance-critical paths where a freshly allocated buffer's ownership is transferred directly.
3. **`DEC-BIFURCATED-UUID` (Bifurcated UUID Semantics — `CryptographicRandom` vs. `SequentialTimeBased`):**
   - `[GuidId]` requires choosing between uncorrelatable random UUIDv4 (for identities and wire-visible tokens where creation timestamps must not leak) and time-ordered UUIDv7 (for local database append-only records to avoid B-Tree page fragmentation).
