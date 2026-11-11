<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->

# Meta-Specification Governance (`spec.spec.md`)

This document is the **meta-specification** governing all technical specification files (`*.spec.md`) and all governed source files within `Percolator.Desktop`. Every specification in this project MUST strictly comply with these meta-rules to ensure architectural consistency, eliminate duplication, and prevent specification drift.

---

## 1. Conformance Clause & Normative Terminology

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **NOT RECOMMENDED**, **MAY**, and **OPTIONAL** in this document and in all child specifications are to be interpreted as described in **BCP 14 ([RFC 2119](https://datatracker.ietf.org/doc/html/rfc2119), [RFC 8174](https://datatracker.ietf.org/doc/html/rfc8174))** when, and only when, they appear in all capitals.

Any statement lacking an all-capitals keyword is informative context, not a normative requirement.

---

## 2. Mandatory Line 1 Governance Headers

Every specification file and every source file governed by a specification MUST declare its governing authority on **line 1**:

### 2.1 For Specification Files (`*.spec.md`)
```markdown
<!-- SPECIFICATION GOVERNANCE: Governed by Percolator.Desktop/spec/spec.spec.md -->
```

### 2.2 For C# Source Files (`*.cs`)
```csharp
// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/<SpecName>.spec.md before modifying this file.
```

### 2.3 For XAML Files (`*.axaml`, `*.xaml`)
```xml
<!-- SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/<SpecName>.spec.md before modifying this file. -->
```

Modifying any governed file without first loading and verifying compliance with its declared specification is strictly prohibited.

---

## 3. Core Meta-Rules

| ID | Meta-Rule | Normative Requirement | Rationale (ISO/IEC/IEEE 29148) |
| :-: | :--- | :--- | :--- |
| **[META-001]** | **Single Source of Truth (DRY)** | Every technical invariant, design token, or pattern MUST be defined in **exactly one** authoritative specification. Child specs MUST link via markdown; they MUST NOT paraphrase or duplicate. | Paraphrased rules drift out of sync, creating conflicting instructions for developers and AI agents. |
| **[META-002]** | **Evidence-Based Grounding** | All rules, APIs, and code samples MUST be verified against official framework documentation, active source code, or explicit human approval. Spec authors MUST NOT rely on ungrounded AI memory. | Eliminates hallucinations, deprecated APIs, and library version incompatibilities. |
| **[META-003]** | **Strict DAG Acyclicity** | Specification dependencies MUST form a strict Directed Acyclic Graph (DAG) as recorded in [`spec/README.md`](./README.md). A specification MAY ONLY depend upon specifications at lower or peer foundational tiers. Circular dependencies are strictly prohibited. | Preserves layered architecture and prevents cascading specification regressions. |
| **[META-004]** | **Decoupled Branch Independence** | View specifications (`View.spec.md`, `CodeBehind.spec.md`) MUST NOT depend on ViewModel specifications (`ViewModel.spec.md`). ViewModel specifications MUST NOT depend on View or Style specifications. | Keeps presentation mechanics completely decoupled from business logic and state testing. |
| **[META-005]** | **Permanent Prefixed Rule IDs** | Every normative invariant in a specification MUST have a permanent, monotonic identifier using its document-specific prefix (e.g. `[META-001]`, `[VIEW-001]`, `[CB-001]`). Prefixes MUST match the registry in [`spec/README.md`](./README.md). | Guarantees immutable traceability across source code comments, PR reviews, and tests. |
| **[META-006]** | **Add-Only Specification Invariant** | Specifications MUST be add-only. Normative rules MUST NEVER be deleted. When an invariant is superseded or obsolete, it MUST be marked `DEPRECATED` with its permanent ID retained. | Completely prevents silent AI rule erosion and broken cross-specification references. |
| **[META-007]** | **Isolated Commits for Clarifications** | Rephrasing or clarifying the wording of an existing invariant (without altering its normative intent) MUST be committed in its own dedicated Git commit separate from code changes or rule additions. | Enables clean Git bisect, transparent audit history, and clear change tracking. |
| **[META-008]** | **Pruned Single-Line Deprecation Tombstones** | When an invariant is marked `DEPRECATED`, its explanatory prose and micro-diffs MUST be pruned and replaced with a single-line tombstone stating the retirement reason and any successor ID. | Conserves AI context window token budget while maintaining unbroken historical ID stability. |
| **[META-009]** | **Zero-Tolerance Anti-Shortcut Discipline** | Specifications MUST explicitly prohibit AI corner-cutting: nullable required dependencies, synthetic entity faking, service locator peeking, and mock-only visual states. | Forces production-grade, testable, type-safe architecture at all times. |
| **[META-010]** | **Mandatory Paired Invariant Tables** | Every specification MUST start with a **MUST DO / MUST NOT DO** quick-reference table summarizing its core operational boundaries. | Enables instant pre-flight validation by humans and AI before writing code. |
| **[META-011]** | **Documented AI Traps** | Every specification MUST maintain a dedicated section identifying common failure modes, lazy patterns, and subtle AI edge-case errors for that domain. | Prevents recurring AI errors (e.g., struct copies in value-type bags, missing XAML scope annotations). |
| **[META-012]** | **Compilable Micro-Diffs (`❌ BAD` vs `✅ GOOD`)** | Every non-trivial invariant MUST provide compilable before/after code snippets illustrating the violation and the approved solution. Snippets MUST be valid C# 13 or Avalonia 11 XAML. | Code diffs provide concrete anchors that eliminate ambiguity. |
| **[META-013]** | **Verified Citations Section** | Every specification MUST conclude with a `## Citations & Verified Sources` section listing permanent documentation URLs or repository source paths. | Enables future sessions to audit and verify requirements without guessing. |
| **[META-014]** | **Zero Speculative Bloat (YAGNI)** | Specifications MUST govern only technologies, controls, and workflows actively utilized or scheduled for the immediate milestone. | Eliminates speculative clutter and maximizes token efficiency. |

---

## 4. Document Prefix Registry Authority

To eliminate specification redundancy per **[META-001]**, all document prefixes (`META-`, `STYLE-`, `VIEW-`, `CB-`, `R3-`, `VM-`), their authoritative domain boundaries, and allowed upstream DAG dependencies are formally and exclusively defined in:

👉 **[`spec/README.md#2-document-prefix-registry--authoritative-domains`](./README.md#2-document-prefix-registry--authoritative-domains)**

Child specifications MUST NOT re-define or re-declare domain prefix registries.

---

## 5. Micro-Diff Examples: Bad vs. Good

### 5.1 Cross-Specification Referencing (Single Source of Truth)
```markdown
<!-- ❌ BAD: Restating tokens in a view spec creates duplication and specification drift -->
All card borders should use a border brush of #2D2D2D and have a corner radius of 8.

<!-- ✅ GOOD: Linking to the authoritative specification via permanent rule ID -->
Card containers MUST bind border and corner geometry to the tokens defined in 
[Style.spec.md](./Style.spec.md) ([STYLE-004] and [STYLE-005]).
```

### 5.2 Deterministic Normative Phrasing
```markdown
<!-- ❌ BAD: Conversational, non-binding prose invites AI corner-cutting -->
It is usually best to try to avoid writing button click handlers in the code-behind if you can.

<!-- ✅ GOOD: Deterministic RFC 2119 / RFC 8174 constraint with permanent rule ID -->
[CB-001] Button clicks MUST be bound to an `IRelayCommand` on the ViewModel via XAML compiled bindings. 
Code-behind MUST NOT handle user intent gestures.
```

### 5.3 Rule Deprecation (Tombstone vs. Silent Deletion)
```markdown
<!-- ❌ BAD: Silently deleting an obsolete rule breaks historical references and erodes specification auditability -->
(Rule deleted from table without a trace)

<!-- ✅ GOOD: Add-only deprecation with a single-line pruned tombstone -->
| [VIEW-005] | DEPRECATED | Superseded by [VIEW-012] (custom drawer replaced by native SplitView). |
```

---

## 6. Specification Amendment & Blast Radius Protocol

Any proposed modification to a specification MUST follow this four-step protocol:

1. **Evidence Verification**: The author MUST cite a verified failure (compilation failure, test failure, profiling snapshot), an official framework document URL, or an explicit user design decision.
2. **Blast Radius Audit**: The author MUST inspect all downstream dependent specifications in the DAG (as recorded in [`spec/README.md`](./README.md)) and all source files governed by the amended specification to confirm that existing implementations remain compliant or are scheduled for alignment.
3. **Add-Only Enforcement**: New invariants MUST receive the next monotonic ID under that document's prefix. Existing invariants MUST NOT be deleted; retired invariants MUST be converted to single-line tombstones ([META-008]).
4. **Isolated Commit**: Any clarification or rewording of an existing rule MUST be saved in its own dedicated Git commit ([META-007]).

---

## 7. Citations & Verified Sources

* **IETF BCP 14 ([RFC 2119](https://datatracker.ietf.org/doc/html/rfc2119) & [RFC 8174](https://datatracker.ietf.org/doc/html/rfc8174))**: *Key words for use in RFCs to Indicate Requirement Levels* and *Ambiguity of Uppercase vs Lowercase in RFC 2119 Key Words*. Definitive standard for normative requirement keywords.
* **ISO/IEC/IEEE 29148:2018**: *Systems and software engineering — Life cycle processes — Requirements engineering*. Establishes mandatory characteristics of requirements: Unambiguous, Complete, Consistent, Singular, and Verifiable.
* **W3C QA Framework: Specification Guidelines**: [W3C Working Group Note](https://www.w3.org/TR/qa-spec/). Best practices for defining conformance clauses, normative versus informative distinction, modularity, and scope boundaries.
* **The Pragmatic Programmer (Hunt & Thomas)**: *Don't Repeat Yourself (DRY)*. Mandates every piece of knowledge must have a single, unambiguous, authoritative representation within a system.
* **Google Engineering Practices**: [Readability Standards & Anti-Patterns](https://google.github.io/eng-practices/). Grounding rules in concrete bad/good code diffs and eliminating conversational ambiguity.
