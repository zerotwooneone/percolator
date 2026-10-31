# Deployable Requirements Specification (`deployable-requirements-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Deployable Requirements Specification** (`deployable-requirements`) document defines the black-box responsibility boundary, architectural segregation role, and public contract obligations of a single **Deployable** (an IEC 62304 *Software Item*, corresponding to a compiled assembly/project).

While `system-requirements` defines what the entire integrated system must do and `design` explains how a deployable works internally, `deployable-requirements` defines **why this deployable exists as a separate assembly** and **what contracts and boundary constraints it enforces at its public edge** (**IEC 62304 Clauses 5.3.1–5.3.4**, **ISO 14971 Clause 7.2**, **FDA 21 CFR 820.30(c)–(d)**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `DEP-<SLUG>`, tagged with `[Verify: Unit | Integration | Simulation | StaticAnalysis]` per [`qms-spec`](qms-spec.md#33-lightweight-verification-method-tags-verify-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts).
- **Permitted Outbound QMS References:** `system-requirements` (`SYS-*`), `risk-register` (`RCM-*`), `fact` (`FACT-*`), and inward `deployable-requirements` (`DEP-*`) strictly matching compile-time project dependencies.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use`, `user-needs`, `design` documents, or outward/peer `deployable-requirements` outside its compile-time dependency graph.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Assembly-Edge Verification Boundary (Boundary "What", Never Internal "How"):**
   - *Include:* The deployable's architectural segregation rules (what dependencies/side-effects are forbidden inside it per *IEC 62304 Clause 5.3.4*), the `SYS-*` and `RCM-*` obligations allocated to it, and the semantic invariants of its public ports and types.
   - *Exclude Internal `design` Details:* Do not describe internal classes, private/internal algorithms, state machine mechanics, or storage schemas here; those belong in the deployable's `design` document.
2. **DRY Kernel Inheritance:** If a boundary rule (such as zero primitive obsession or deterministic clock injection) is already mandated by an inward kernel deployable, reference that inward `DEP-<SLUG>` once rather than restating the rule in every downstream deployable.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <Deployable Name> Requirements`
`Spec: [deployable-requirements-spec](<relative-path>/spec/deployable-requirements-spec.md)`

### `## 1. Segregation Role and Boundary Constraints`
Explains why this deployable is isolated as its own assembly (`DEP-<SLUG>`, IEC 62304 Clause 5.3.4) and what side effects, frameworks, or outward dependencies are strictly forbidden inside it.

### `## 2. Allocated Responsibilities`
The atomic, black-box functional and security obligations (`DEP-<SLUG>`) allocated to this deployable from `SYS-*` and `RCM-*`.

### `## 3. Public Contract Invariants`
The semantic guarantees, thread-safety expectations, error/result conventions, and resource-ownership rules (`DEP-<SLUG>`) required across the public ports and types exposed by this deployable (IEC 62304 Clause 5.3.2).
