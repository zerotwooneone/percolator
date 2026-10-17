---
name: architectural-plan-refinement
description: >-
  Use this skill whenever drafting, reviewing, or refining an implementation plan
  for new features, protocol components, or architectural refactors. Enforces a
  multi-turn, lens-by-lens refinement protocol (Scope, Onion Architecture, Threat
  Modeling, Unit Testing, and Infrastructure Alignment) before any production code
  is written.
---

# Multi-Turn Architectural Plan Refinement Protocol

This skill enforces disciplined, phased plan refinement for the Percolator architecture. It prevents premature coding, hallucinations, speculative bloat, and architectural erosion by dividing refinement into dedicated, single-focus conversational turns.

## Cardinal Rules

1. **Gated Turn Execution**:
   **DO NOT WRITE PRODUCTION CODE WHILE REFINING A PLAN.**
   Implementation tools (`client_create_file`, `client_edit_file`, patch operations on source code) are strictly prohibited until all refinement lenses have been evaluated, recorded in the plan, validated by script, and explicitly approved by the user.

2. **Single-Lens Focus**:
   Every refinement turn must evaluate the plan through **exactly one lens**. Never attempt to refine multiple lenses in a single turn.

3. **Mandatory Interactive Modal (`ask_question`)**:
   At the conclusion of each turn, you must invoke the `ask_question` tool (or provide explicit options if blocked) to halt execution and obtain user sign-off before proceeding.

---

## State Recovery & Resumption Across Turns

When starting or resuming refinement (e.g., after context compaction or session interruption):
1. **Locate the Plan**: Open the target `implementation-plan.md`.
2. **Inspect the Status Gate**: Check the `## Refinement Status Gate` checkboxes:
   - The first lens marked `[ ]` (Pending) is your active turn.
   - If all 5 lenses are `[x]` (Approved) and `Implementation Gate: UNLOCKED`, refinement is complete and implementation may begin.
3. **Execute Active Lens**: Proceed exclusively with that lens.

---

## The 5 Refinement Lenses

### Lens 1: Scope & Feature Slice Framing (Turn 1)
- **Excise Speculative Complexity**: Actively detect and remove unrequested features, speculative protocols (e.g., unexpected network discovery scans or uncoordinated DHTs), and premature optimizations.
- **Vertical Feature Slices**: Group code and plans by feature capabilities/behaviors (e.g., `Features/<FeatureName>/`), never by technical layers (e.g., horizontal folders of "Models", "Services", "Helpers").
- **Orchestration Decomposition**: Verify that multi-step operations (e.g., candidate discovery + connection) are modeled as distinct, composable workflow steps rather than monolithic methods.

### Lens 2: Onion Architecture & Primitive Obsession (Turn 2)
- **Shared Kernel & Domain Purity**: Feature slices must never pollute shared core domain aggregates. Domain aggregates, entities, and ports must have zero direct dependencies on concrete databases, transport sockets, or serialization frameworks.
- **Aggregate Completeness & Boundary Gatekeeping**: Aggregate roots must encapsulate all cryptographic keys, nonces, and participants needed to validate their lifecycle transitions (anti-anemic). Child entities must be accessed via read-only interfaces, and repositories must persist aggregate roots atomically with zero partial side-door methods.
- **Eliminate Primitive Obsession**: Any naked primitive (`string`, `byte[]`, `ReadOnlyMemory<byte>`, `int`, `long`, `Guid`, `bool`) representing a domain concept (tokens, limits, topologies, roles, policies, offsets) must be encapsulated in a strongly typed Value Object or enum.
- **Ban Flag Arguments & Nullable Doublets**: Never use boolean flags for control coupling or method flow control. Model mutually exclusive state variants via discriminated unions or polymorphic domain records instead of paired nullable properties.
- **Ban Implicit/Explicit Cast Operators**: Value objects must provide zero `implicit` or `explicit` cast operators. Construction must use explicit factory methods (`Create`, `FromX`, `Parse`), access must be through named properties, and arithmetic must be encapsulated within domain methods.

### Lens 3: Threat Modeling & Security Posture (Turn 3)
- **Protocol Parity**: Preserve core cryptographic invariants matching or exceeding Signal Protocol standards (e.g., asynchronous store-and-forward, zero online/offline presence tracking, zero activity leases).
- **Resource Starvation Defenses**: Audit for prekey exhaustion (e.g., One-Time Prekey starvation attacks), directory spamming, and unbounded resource consumption.
- **Hard Admission & Anti-Impersonation**: Require mutual authenticated handshakes and active Signed PreKeys before granting directory admission. Enforce cryptographic identity verification (`payload.SenderId == context.SenderIdentityId`).
- **Route & Metadata Privacy**: Mask public IP addresses behind relayed delivery tokens on wide-area networks. Ensure private rendezvous uses time-ratcheted deterministic blinded locators across discrete epoch windows.

### Lens 4: High-Value, Low-Brittle Unit Test Design (Turn 4)
- **Behavioral State & Admission Matrices**: Design test cases around state transitions, authorization boundaries, deterministic locator calculation across epoch boundaries, and fault tolerance.
- **Ban Brittle Test Anti-Patterns**:
  - Do NOT test private implementation details.
  - Do NOT verify exact mock invocation counts (e.g., `Verify(x => x.Method(), Times.Once())`) unless testing idempotency.
  - Do NOT assert serialized JSON/string formatting.
  - Test observable domain outcomes, state changes, and error handling.
- **Fault Tolerance**: Verify that failures on individual items in batch or discovery workflows do not abort the overall operation.

### Lens 5: Cross-Layer Infrastructure Alignment (Turn 5)
- **Contract & Port Alignment**: Audit infrastructure plans (`Percolator.Infrastructure/implementation-plan.md`) to verify that all ports defined in the feature app have matching infrastructure adapters.
- **Excise Legacy Artifacts**: Remove obsolete infrastructure services, background pruners, or routing tables that were superseded by the refined design.
- **Schema Parity**: Ensure Protobuf definitions, SQLite tables, and gRPC endpoints precisely match the domain payloads and value object contracts.

---

## Backtracking & Revision Protocol

If evaluating a later lens reveals a defect or requirement that affects an earlier lens:
1. **Regress the Affected Lens**: Revert that lens's status gate from `[x]` (Approved) back to `[ ]` (Pending).
2. **Document the Impact**: In your turn summary, explain why the earlier lens must be revisited.
3. **Re-evaluate**: Return to the earliest pending lens in the sequence to restore architectural consistency.

---

## Automated Validation & Turn Completion

Before presenting your turn results to the user:
1. **Run the Plan Validator**:
   Execute the automated validation script to verify the plan's structural integrity and anti-pattern rules:
   ```powershell
   pwsh -File ".agents/skills/architectural-plan-refinement/scripts/validate-plan.ps1" -Path "<path-to-plan>"
   ```
2. **Present Turn Findings**:
   Provide a concise summary:
   - What was identified or refactored under the active lens.
   - Architectural trade-offs or design decisions made.
   - Status of the automated validation check.
3. **Interactive Checkpoint**:
   Call the `ask_question` tool with options:
   - "(Recommended) Approve this lens and proceed to the next lens"
   - "Revise this lens (specify feedback)"

---

## Reference Materials

- **Automated Validator**: [validate-plan.ps1](./scripts/validate-plan.ps1)
- **Canonical Reference Implementation**: [gold-standard-plan.md](./examples/gold-standard-plan.md)
- **Detailed Lens Checklists**: [refinement-lenses.md](./references/refinement-lenses.md)
- **Implementation Plan Template**: [plan-template.md](./references/plan-template.md)
