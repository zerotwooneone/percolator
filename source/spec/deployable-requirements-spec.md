# Deployable Requirements Specification (`deployable-requirements-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Deployable Requirements Specification** (`deployable-requirements`) document defines the black-box responsibility boundary, architectural segregation role, and public contract obligations of a single **Deployable** (an IEC 62304 *Software Item*, corresponding to a compiled assembly/module).

While `system-requirements` defines what the entire integrated system must do and `design` explains how a deployable works internally, `deployable-requirements` defines **why this deployable exists as a separate module** and **what contracts and boundary constraints it enforces at its public edge** (**IEC 62304 Clauses 5.3.1–5.3.4**, **ISO 14971 Clause 7.2**, **FDA 21 CFR 820.30(c)–(d)**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `DEP-<SLUG>`, tagged with `[Verify: Unit | Integration | StaticAnalysis]` per [`qms-spec`](qms-spec.md#33-lightweight-verification--risk-control-tags-verify--control-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts).
- **Permitted Outbound QMS References:** `system-requirements` (`SYS-*`), `soup` (`SOUP-*`), `fact` (`FACT-*`), and inward `deployable-requirements` (`DEP-*`) strictly matching compile-time project dependencies.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use`, `user-needs`, `risk-register` directly (traces through `SYS-*`), `design` documents, or outward/peer `deployable-requirements` outside its compile-time dependency graph.
- **Software Item Interface Referencing Rule (IEC 62304 Clause 5.3.2):** An adapter deployable requirement (`DEP-*`) may reference public port interfaces (`IPortName`) and domain value objects defined by an **inward compile-time dependency** that it implements or encapsulates. It **MUST NOT** reference interfaces from sibling or outward deployables (preserving Dependency Inversion), and **MUST NOT** cite concrete realizing types (which belong in `Design.md`).

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Module-Edge Verification Boundary (Boundary "What", Never Internal "How"):**
   - *Include:* The deployable's architectural segregation role (what frameworks, dependencies, or side effects are forbidden inside it per *IEC 62304 Clause 5.3.4*), the `SYS-*` obligations allocated to it, any third-party SOUP components ([`SOUP-*`](soup-spec.md)) it encapsulates, and the semantic invariants of its public ports and types.
   - *Strict Prohibition on Concrete Types & Internal Details:* Concrete implementing classes, records, structs, internal database table names, or private file paths **MUST NOT** appear in `deployable-requirements` (`DEP-*`). Requirements must be defined strictly against public port interfaces (`IPortName`), domain value objects, or abstract module boundaries. Concrete realizing types, algorithms, and storage schemas belong exclusively in `Design.md` (IEC 62304 Clause 5.4 Detailed Design).
2. **Two Software Item Archetypes (Clean / Onion Architecture):**
   - **Domain Core Deployables (`Domain`, `Domain.Kernel`):** Boundary constraints in Section 1 **MUST** prohibit database engines, network transports, OS-specific APIs, UI frameworks, and third-party SOUP.
   - **Adapter Deployables (`Infrastructure`, `Desktop`, `Simulator`):** Boundary constraints in Section 1 **MUST** prohibit domain business rules and forbid unquarantined SOUP handles (`IntPtr`, `SafeHandle`), vendor DTOs, or native exceptions from crossing domain port implementations.
3. **DRY Kernel Inheritance:** If a boundary rule is already mandated by an inward kernel deployable, reference that inward `DEP-<SLUG>` once rather than restating the rule in every downstream deployable.
4. **Traceability Completeness Rule (IEC 62304 Clause 5.3.1):** Every system requirement (`SYS-*`) that is realized by this deployable's design outputs (`Design.md`) **MUST** be explicitly allocated in Section 2 (`Allocated Responsibilities`) or Section 3 (`Public Contract Invariants`). No deployable design output may cite a `SYS-*` without an allocated `DEP-*` establishing the software item requirement.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <Deployable Name> Requirements`
`Spec: [deployable-requirements-spec](<relative-path>/spec/deployable-requirements-spec.md)`

### `## 1. Segregation Role and Boundary Constraints`
Explains why this deployable is isolated as its own module (`DEP-<SLUG>`, IEC 62304 Clause 5.3.4) and what side effects, frameworks, or outward dependencies are strictly forbidden inside it.

### `## 2. Allocated Responsibilities`
The atomic, black-box functional and risk-control obligations (`DEP-<SLUG>`) allocated to this deployable from `SYS-*`. If this deployable encapsulates third-party SOUP components ([`SOUP-*`](soup-spec.md)), it **MUST** define the allocated requirement that wraps and isolates those capabilities.

### `## 3. Public Contract Invariants`
The semantic guarantees, concurrency model (declaring stateless reentrancy vs. single-threaded/scoped lifecycle per IEC 62304 Clause 5.3.2), error/result conventions, and resource-ownership rules (`DEP-<SLUG>`) required across the public ports and types exposed by this deployable.
