# DIRECTIVE: SYSTEM STANDARDS FOR ROBUST UNIT TESTING

**Context:** This document defines the operational standards for generating unit tests. The primary objectives are:
1. **Minimize Brittleness:** Prevent tests from failing when internal implementation details change while external behavior remains correct.
2. **Maximize Token Economy:** Avoid spending AI reasoning and generation tokens writing low-value, ritualistic, or redundant tests.
3. **Target High-Value Invariants:** Focus test coverage on domain rules, state machine transitions, edge cases, and failure modes.

**Core Principle:** Test public behavior and domain invariants, not compiler mechanics or internal wiring.

---

## 1. The TDD Algorithm: Red, Green, Refactor

Adhere strictly to the **Red-Green-Refactor** cycle when implementing domain models, business logic, and services.

### Phase 1: RED (The Failing State)
* **Action:** Write the test *before* the implementation logic.
* **Constraint:** The test must assert a requirement or invariant that does not yet exist.
* **Validation:** Run the test. It **must** fail. (A logic/assertion failure is preferred over compilation failure).
* **Purpose:** Ensures the test genuinely detects the absence of the feature.

### Phase 2: GREEN (The Passing State)
* **Action:** Write the implementation code.
* **Constraint:** Write *only* the minimum code required to make the test pass. Avoid premature optimization or unrequested features.
* **Validation:** Run the test. It **must** pass.

### Phase 3: REFACTOR (The Cleanup)
* **Action:** Clean up code structure, readability, and allocation efficiency.
* **Constraint:** Do not alter external behavior or contracts.
* **Validation:** Rerun tests to ensure all remain green.

---

## 2. High-Value vs. Low-Value Tests (AI Token Economy)

AI code generators frequently waste tokens writing boilerplate tests that provide zero defect prevention. Follow this strict matrix:

| Category | Instruction | Why / Examples |
| :--- | :--- | :--- |
| **Constructor Field Assignments** | ❌ **BANNED** | Do not test `new PublicIdentityId(x).Value == x`. This tests the C# compiler, not your code. |
| **Auto-Properties / Getters & Setters** | ❌ **BANNED** | Zero defect risk. Consumes tokens without verifying business logic. |
| **Language Features / Record Equality** | ❌ **BANNED** | Do not test that C# records have structural equality or that enums hold values. |
| **Framework / Mock Wiring Checks** | ❌ **BANNED** | Do not assert that a mocked interface returns the value you just mocked it to return. |
| **Domain Invariants & Boundaries** | ✅ **MANDATORY** | E.g., Ratchet rejects skipping > 2000 keys; admin role required to advance epoch. |
| **State Machine Transitions** | ✅ **MANDATORY** | E.g., `Active` -> `Disabled` pauses outbox, zeroes memory, and emits `IdentityDisabledEvent`. |
| **Domain Result / Error Codes** | ✅ **MANDATORY** | E.g., Committing stale base epoch returns `DomainResult.Failure(EpochConflict)`. |
| **Temporal Expirations & TTL** | ✅ **MANDATORY** | E.g., Envelopes past `ExpiresAtUtc` are dropped by `RelayMailboxQueue`. |

---

## 3. Anti-Bloat Techniques for AI Generators

### Rule A: Parameterized Tests Over Method Duplication
Do not generate 5 separate test methods to check nulls, empties, whitespace, or numerical bounds. Always consolidate using NUnit `[TestCase]`:

```csharp
// ❌ BAD: Consumes 100+ tokens across multiple repetitive test methods
[Test] public void Validate_WhenNull_Fails() { ... }
[Test] public void Validate_WhenEmpty_Fails() { ... }
[Test] public void Validate_WhenWhitespace_Fails() { ... }

// ✅ GOOD: Compact, token-efficient, single test method
[TestCase(null)]
[TestCase("")]
[TestCase("   ")]
public void CreateProfile_WithInvalidName_ReturnsValidationError(string? invalidName)
{
    var result = IdentityProfile.Create(PublicIdentityId.New(), invalidName!);
    result.IsFailure.Should().BeTrue();
}
```

### Rule B: Assert the Delta, Not the Universe
Only assert the specific state change or value produced by the `Act` step. Do not check every untouched property of the System Under Test (SUT).

```csharp
// ❌ BRITTLE: Asserts every property; breaks if unrelated defaults change
result.Id.Should().Be(id);
result.CreatedAtUtc.Should().Be(now);
result.UnrelatedMetadata.Should().BeEmpty();

// ✅ ROBUST: Asserts only what the Act changed
result.State.Should().Be(IdentityState.Disabled);
result.DomainEvents.Should().ContainSingle(e => e is IdentityDisabledEvent);
```

### Rule C: Use Test Data Factories (Object Mothers)
Never instantiate a complex 15-line aggregate graph in the `Arrange` block of every single test. Extract a private or shared factory method:

```csharp
// ✅ Clean Arrange step using a factory helper
var conversation = CreateActiveGroupWithAdmin(adminId);
```

---

## 4. Anatomy of a Non-Brittle Test

A "brittle" test breaks when internal implementation details are refactored, even though business behavior is intact. Follow the **AAA Pattern** and the **Black Box Rule**.

### The Structure: AAA (Arrange, Act, Assert)
Visually separate the three steps with clear spacing or comments:
1. **Arrange:** Setup the inputs, mocks, and SUT.
2. **Act:** Execute the specific business method.
3. **Assert:** Verify the result (return value or public state delta).

### The Black Box Rule
Treat the SUT as a black box:
* **DO** assert public return values (`DomainResult`, outputs).
* **DO** assert public state changes and published domain events.
* **DO NOT** use reflection to inspect private fields or backing collections.
* **DO NOT** verify internal private helper invocations.
* **DO NOT** assert strict execution order unless ordering is an explicit business requirement.

---

## 5. Mocking Constraints

Over-mocking creates brittle, change-resistant tests.

| Scenario | Instruction | Reason |
| :--- | :--- | :--- |
| **External I/O (Database, Network, gRPC)** | **MOCK / FAKE IT** | Tests must be in-memory, deterministic, and fast (< 50ms). |
| **Value Objects & Domain Aggregates** | **USE REAL OBJECTS** | Mocking entities creates false confidence and masks real invariant bugs. |
| **Pure Ports (Crypto, ZK Proofs)** | **USE DETERMINISTIC DOUBLES** | Use predictable in-memory fakes (e.g. `DeterministicCryptoEngine`) over heavy native binaries. |
| **Strict Method Verification (`Verify`)** | **AVOID UNLESS SIDE-EFFECT IS PRIMARY GOAL** | Avoid checking `Verify(x => x.InternalStep())`. Only verify when emitting an external side-effect (e.g., publishing an outbox job). |

---

## 6. The Determinism Rule (Time & Deterministic State)

Tests must be 100% deterministic. They should never fail due to environmental factors, CPU load, timezone shifts, or clock jitter.

* **NEVER** use `DateTime.UtcNow`, `DateTime.Now`, `DateTimeOffset.UtcNow`, `Thread.Sleep`, or `new Random()` inside tests.
* **ALWAYS** inject a controlled time double (`FakeDateTimeProvider` or `TimeProvider`).
* **ALWAYS** advance virtual time manually (`timeProvider.Advance(TimeSpan.FromHours(1))`) when testing TTLs, timeouts, or lease expirations.

---

## 7. AI Code-Generation Verification Checklist

Before emitting unit tests, the AI must verify:
1. **Is this test asserting an invariant or state machine transition?** (If it's just asserting a constructor field assignment, delete it).
2. **Can multiple edge cases be expressed with `[TestCase]`?** (If so, collapse them).
3. **Does the test rely only on public contracts?** (No reflection, no internal private method mocks).
4. **Is time fully virtualized and deterministic?** (No wall-clock dependencies).
5. **Is the test name descriptive of the behavior and context?** (e.g., `CommitMutation_WhenBaseEpochMismatched_ReturnsEpochConflict`).
