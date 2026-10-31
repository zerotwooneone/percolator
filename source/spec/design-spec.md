# Design Specification (`design-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Design Specification** (`design`) document—placed as `Design.md` at the root of a deployable project—is the high-signal architectural guide (**Software Design Output**) for that deployable (IEC 62304 *Software Item*). Its purpose is to give engineers and AI assistants immediate context on the deployable's layer boundary, internal invariants, and non-obvious technical design choices **without having to read every source file** and **without breaking every time a feature is added or a bug is fixed** (**IEC 62304 Clauses 5.3 & 5.4**, **ISO 13485 Clause 7.3.4**, **FDA 21 CFR 820.30(d)**, **ISO 14971 Clauses 7.1 & 7.2**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefixes:**
  - `INV-<SLUG>` for architectural invariants (Section 2)
  - `DEC-<SLUG>` for key technical design choices (Section 3)
  - Governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts) (no deletion, renaming, or weakening without human approval).
- **Permitted Outbound QMS References:** Its corresponding `deployable-requirements` (`DEP-*`) or `system-requirements` (`SYS-*`), `risk-register` (`RCM-*`), `fact` (`FACT-*`), and **inward `design` documents** (`INV-*`, `DEC-*`) strictly matching compile-time project references.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` or `user-needs` directly, and **MUST NOT** link to outward or peer `design` documents that this project does not depend on at compile time (use non-authoritative [cross-cutting tags](qms-spec.md#4-non-authoritative-cross-cutting-tags-tag-name) instead).

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Internal Architecture & Trade-Offs Only:**
   - *Include:* The deployable's architectural boundary (In-Scope vs. Out-of-Scope segregation), enduring internal encapsulation/concurrency/state-ownership invariants (`INV-<SLUG>`), and non-obvious engineering mechanisms (`DEC-<SLUG>`) together with *Rejected Alternatives* explaining why simpler or naïve designs were ruled out.
   - *Exclude Upstream Problem/Requirement Narratives:* Do not re-state user stories or black-box system requirements; explain *how* this deployable's internal design satisfies them.
2. **Zero Intra-Document Repetition Across Sections:**
   - Each of the 3 allowed sections has a distinct job. If a concept is listed as an In-Scope responsibility in Section 1, do not re-list it as a folder/module in Section 2 and re-explain it in Section 3.
3. **Define Shared Rules Once in the Innermost Deployable (DRY Inheritance):**
   - Cross-cutting architectural invariants (such as primitive-obsession guards, result types, clock inversion, or persistence rehydration visibility) **MUST** be defined **once** in the innermost deployable's `Design.md`. Downstream `Design.md` files may link inline to that inward `INV-<SLUG>` or `DEC-<SLUG>` when extending the pattern, never restating it.

---

## 4. Allowed Headings and Section Definitions

An implementing `Design.md` **MUST** use only the three H2 headings below in this exact order and **MUST NOT** repeat these section explanations:

### `# <Deployable Name>`
`Spec: [design-spec](<relative-path>/spec/design-spec.md)`

### `## 1. Purpose and Layer Boundary`
States why this deployable exists as a distinct module in the architecture, what responsibilities belong inside it (**In Scope**), and what responsibilities are strictly excluded to inward or outward layers (**Out of Scope** segregation boundary per IEC 62304 Clause 5.3.4).

### `## 2. Architectural Invariants and Encapsulation`
Defines the timeless structural, visibility, concurrency, and state-ownership rules (`INV-<SLUG>`) that all code inside this deployable must obey, linking inline to an inward `Design.md` for inherited kernel rules rather than repeating them.

### `## 3. Key Technical Design Choices`
Records the non-obvious architectural mechanisms and trade-offs (`DEC-<SLUG>`) chosen to solve core problems in this deployable—along with brief *Rejected Alternatives* where it prevents regression—without repeating Section 1/2 or hardcoding tunable code constants.
