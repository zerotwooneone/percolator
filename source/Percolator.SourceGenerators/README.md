# Percolator.SourceGenerators

`Percolator.SourceGenerators` is a compile-time Roslyn incremental source generator library (`netstandard2.0`) for the Percolator solution. It generates zero-allocation, length-constrained byte array value objects and strongly typed GUID entity/token identifiers, eliminating primitive obsession while guaranteeing build-time safety.

For full architectural boundaries, diagnostic rules (`PERC001`–`PERC104`), and technical decisions (such as constant-time equality and UUID generation strategies), see the project [Design Document](Design.md).

---

## Capabilities & Usage

### 1. Byte Array Value Objects (`[ByteArray]`)
Annotate a `sealed partial record` to generate an immutable, length-constrained byte wrapper backed by `ReadOnlyMemory<byte>`:

```csharp
using Percolator.SourceGenerators;

// Exact 32-byte constraint (e.g. SHA-256 hash or Ed25519 key)
[ByteArray(32)]
public sealed partial record Sha256Hash;

// Range-constrained bytes (e.g. variable-length salt or token)
[ByteArray(minLength: 16, maxLength: 64)]
public sealed partial record EphemeralNonce;
```

**Generated Features:**
- `FromBytes(byte[])` and `FromSpan(ReadOnlySpan<byte>)` with defensive copies and length validation.
- `FromBytesOwned(byte[])` for zero-copy ownership transfer.
- `TryFromBytes(...)` and `TryFromSpan(...)` non-throwing parsers.
- `Span`, `Memory`, `Length`, and `ToArray()`.
- Constant-time equality (`CryptographicOperations.FixedTimeEquals`) to prevent timing side-channel leaks.

### 2. Strongly Typed Identifiers (`[GuidId]`)
Annotate a `readonly partial struct` or `record struct` to generate a strongly typed domain identifier:

```csharp
using Percolator.SourceGenerators;

// Cryptographically secure random UUIDv4 (for identities and security tokens)
[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial struct IdentityId;

// Monotonically ordered UUIDv7 (for database append-only entities like messages)
[GuidId(GuidIdKind.SequentialTimeBased)]
public readonly partial struct MessageId;
```

**Generated Features:**
- `Value` (`Guid`), `IsValid` (non-empty), and `EnsureValid()` guard.
- `New()`, `FromGuid(Guid)`, `FromBytes(ReadOnlySpan<byte>)`, and `TryWriteBytes(...)`.
- `IComparable<T>`, `IEquatable<T>`, `ISpanFormattable`, and `IParsable<T>`.

---

## Build & Diagnostic Enforcement

The generator enforces strict structural invariants at compile time:
- `PERC001`–`PERC007`: Validates that `[ByteArray]` targets are top-level, non-generic, sealed partial records with valid length bounds.
- `PERC101`–`PERC104`: Validates that `[GuidId]` targets are top-level, non-generic partial structs.
