# DIRECTIVE: SYSTEM STANDARDS FOR ROBUST UNIT TESTING

**Context:** This document defines the operational standards for generating unit tests. The primary objective is to minimize "brittleness"—the tendency of a test to fail when internal implementation details change, even if the external behavior remains correct.

**Core Principle:** Test public behavior, not internal implementation.

---

## 1. The TDD Algorithm: Red, Green, Refactor

You must adhere to the **Red-Green-Refactor** cycle when generating code from scratch or implementing new features.



[Image of red green refactor TDD cycle]


### Phase 1: RED (The Failing State)
* **Action:** Write the test *before* the implementation logic.
* **Constraint:** The test must assert a requirement that does not yet exist.
* **Validation:** Run the test. It **must** fail. (A compilation error counts as a failure in typed languages, but a logic failure is preferred).
* **Purpose:** Ensures the test actually detects the absence of the feature.

### Phase 2: GREEN (The Passing State)
* **Action:** Write the implementation code.
* **Constraint:** Write *only* the minimum amount of code required to make the test pass. Do not implement optimization or extra features yet.
* **Validation:** Run the test. It **must** pass.

### Phase 3: REFACTOR (The Cleanup)
* **Action:** Improve the code structure, readability, and performance.
* **Constraint:** Do not change the external behavior.
* **Validation:** Run the test again. It **must** still pass.
* **Critical:** This applies to test code as well. Refactor tests to be cleaner and drier, provided they stay readable.

---

## 2. Anatomy of a Non-Brittle Test

A "brittle" test is one that requires maintenance whenever the underlying code is refactored. To avoid this, follow the **AAA Pattern** and the **Black Box Rule**.

### The Structure: AAA (Arrange, Act, Assert)
Every test must visually separate these three steps.

1.  **Arrange:** Setup the inputs, mocks, and the system under test (SUT).
2.  **Act:** Execute the specific method or behavior being tested.
3.  **Assert:** Verify the result (state change or return value).

### The Black Box Rule
Treat the System Under Test (SUT) as a black box.
* **DO** assert that the output matches the input.
* **DO** assert that side effects (e.g., database saves, events published) occurred via public interfaces.
* **DO NOT** use reflection to check private fields.
* **DO NOT** assert that internal helper methods were called.
* **DO NOT** assert the order of execution inside the black box, unless order is a business requirement.

---

## 3. Mocking Constraints

Over-mocking is the leading cause of brittleness.

| Scenario | Instruction | Reason |
| :--- | :--- | :--- |
| **External Dependencies** (DB, API, File System) | **MOCK IT** | Tests must be fast and deterministic. |
| **Value Objects / Data Models** | **USE REAL OBJECTS** | Mocking simple data structures adds noise and hides serialization issues. |
| **Internal Private Methods** | **NEVER MOCK** | These are implementation details. Test them via the public method that calls them. |
| **Strict Interaction Checks** | **AVOID** | Avoid `Verify(x => x.Method(), Times.Once)`. Only verify interactions if the *side effect* is the primary goal (e.g., sending an email). |

---

## 4. Code Examples: Brittle vs. Robust

### ❌ BAD: Brittle Test Implementation
*Why it fails:* It is tied to the specific implementation steps. If the developer changes the internal logic (e.g., uses a different loop or helper method) but gets the same result, this test will break.

```csharp
// Scenario: Calculating a cart total
[Test]
public void CalculateTotal_Implementation_Check()
{
    // ARRANGE
    var calculator = new CartCalculator();
    var items = new List<Item> { new Item(10), new Item(5) };

    // ACT
    var result = calculator.Calculate(items);

    // ASSERT
    // BRITTLE: Checking a private field via reflection
    var internalSum = GetPrivateField(calculator, "_currentSum");
    Assert.AreEqual(15, internalSum);

    // BRITTLE: Verifying an internal helper was called exactly once
    // If we optimize to not use the helper, the test fails falsely.
    Mock.Get(calculator).Verify(x => x.RunInternalMathLoop(), Times.Once);
}
```

### ✅ GOOD: Robust Test Implementation
*Why it works:* It focuses purely on inputs and outputs. The developer can completely rewrite the internal math logic, and as long as $10 + $5 = $15, this test passes.

```csharp
// Scenario: Calculating a cart total
[Test]
public void Calculate_GivenMultipleItems_ReturnsSumOfPrices()
{
    // ARRANGE
    var calculator = new CartCalculator();
    var items = new List<Item> { new Item(10), new Item(5) };

    // ACT
    var result = calculator.Calculate(items);

    // ASSERT
    // ROBUST: Only checking the return value (Public Contract)
    Assert.AreEqual(15, result);
}
```

---

## 5. Summary Checklist for AI Generation

Before finalizing code generation, verify:
1.  Does the test name describe the **behavior**, not the method name? (e.g., `UserIsAdmitted_WhenAgeIsOver18` vs `TestAdmitUser`).
2.  Is the test independent? (It does not rely on the state left by a previous test).
3.  Are we testing the **what** (result), not the **how** (implementation)?
4.  Is the Code Coverage focused on logic branches, not just line execution?