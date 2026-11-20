# Percolator.Domain.Kernel

Spec: [design-spec](../spec/design-spec.md)

## 1. Purpose and Layer Boundary

`Percolator.Domain.Kernel` is the shared tactical Domain-Driven Design (DDD) kernel at the center of the Onion Architecture. It exists as a separate assembly so autonomous domain bounded contexts (`Percolator.Domain` and `Percolator.Apps.FileTransfer.Domain`) can share base DDD abstractions and universal identity primitives without coupling to one another.

- **In Scope:** Base aggregate/entity abstractions (`AggregateRoot<TId>`, `IEntity<TId>`, `IDomainEvent`), functional result types (`DomainResult`, `DomainError`), temporal and secret-hygiene contracts (`IDateTimeProvider`, `ISensitiveSecret`), and cross-domain identity tokens (`PublicIdentityId`).
- **Out of Scope:** Any feature-specific domain logic (messaging, ratchets, channels, discovery, or file transfer), external NuGet packages, I/O, or system clocks. A type belongs here only if required by at least two independent domain assemblies.

---

## 2. Architectural Invariants and Encapsulation

1. **`INV-DOMAIN-VALUE-TYPES` (Zero Primitive Obsession Baseline):** Building on [`INV-ZERO-PRIMITIVES`](../Percolator.SourceGenerators/Design.md#2-architectural-invariants-and-encapsulation), all domain identifiers, byte buffers, counters, offsets, and timestamps across the kernel and downstream domains must be represented as self-validating value objects rather than raw primitives.
2. **`INV-TIME-INVERSION` (Explicit Temporal Inversion):** Domain models never query `DateTimeOffset.UtcNow` or `TimeProvider.System` directly; current time is always passed into domain operations via `IDateTimeProvider` or value-object timestamps so state transitions are 100% deterministic.
3. **`INV-SECRET-ZEROIZATION` (Domain-Owned Secret Zeroization):** Any domain model holding sensitive cryptographic material implements `ISensitiveSecret` (`IDisposable` + `IsZeroed`) and is responsible for zeroizing its own memory buffers upon state progression or disposal. `#session-lock`

---

## 3. Key Technical Design Choices

1. **`DEC-STRUCT-DOMAIN-RESULT` (Value-Type `DomainResult<T>` for Expected Business Rejections):**
   - *Design Choice:* Expected domain rule rejections (unauthorized role, invalid state transition, verification failure) return a `readonly record struct` `DomainResult<T>` carrying a structured `DomainError`, reserving exceptions strictly for unrecoverable programmer contract defects.
   - *Rejected Alternative:* Throwing exceptions for domain rule rejections or allocating heap-based result wrappers on hot cryptographic/streaming paths.
2. **`DEC-AGGREGATE-EVENTS` (In-Memory Domain Event Recording on `AggregateRoot<TId>`):**
   - Aggregates record state-transition events in an encapsulated collection (`AddDomainEvent` / `ClearDomainEvents`) without depending on an external event bus or mediator framework inside the domain layer.
