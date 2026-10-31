# Deployable Requirements Specification (`deployable-requirements-spec`)

## 1. Purpose and QMS Basis

A **Deployable Requirements Specification** (`deployable-requirements`) document defines the black-box responsibility boundary, architectural segregation role, and public contract obligations of a single **Deployable** (an IEC 62304 *Software Item*, corresponding to a compiled assembly/project).

While `system-requirements` defines what the entire system must do and `design` explains how a deployable works internally, `deployable-requirements` defines **why this deployable exists as a separate assembly** and **what contracts and boundary constraints it enforces at its edge**.

- **IEC 62304:2006+AMD1:2015 Clause 5.3.1 – 5.3.4** (*Software architectural design & Software item segregation*): Decomposing the system into Software Items (Deployables), allocating requirements, specifying inter-item interface responsibilities, and enforcing segregation between items for risk control.
- **ISO 14971:2019 Clause 7.2** & **FDA 21 CFR 820.30(c)–(d)**: Verifying that risk controls allocated to a component boundary cannot be bypassed by adjacent components.

---

## 2. Dependency Rules

- **Permitted Outbound QMS References:** `system-requirements`, `risk-register` (`RCM-*`), `fact`, and inward `deployable-requirements` documents strictly matching compile-time project dependencies.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use`, `user-needs`, `design` documents, or outward/peer `deployable-requirements` outside its compile-time dependency graph.

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [deployable-requirements-spec](<relative-path>/spec/deployable-requirements-spec.md)`
2. **Never Duplicate `.csproj` or Public C# Interfaces:** Do not list NuGet package versions, `.csproj` `<ProjectReference>` lists, or copy-paste C# `interface` method signatures into this document. The compiler and `.csproj` already own those facts. Document the *semantic contract* and *segregation rules* of the boundary instead.
3. **Never Duplicate Internal `design` Details:** Do not describe internal classes, algorithms, or storage schemas here; those belong in the deployable's `design` document.
4. **Inherit Kernel Rules by Reference:** If a boundary constraint (such as zero primitive obsession or deterministic clock injection) is already mandated by an inward kernel deployable, reference that inward deployable once rather than restating the rule in every downstream deployable.
5. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <Deployable Name> Requirements`
`Spec: [deployable-requirements-spec](<relative-path>/spec/deployable-requirements-spec.md)`

### `## 1. Segregation Role and Boundary Constraints`
Explains why this deployable is isolated as its own assembly (IEC 62304 Clause 5.3.4) and what side effects, frameworks, or outward dependencies are strictly forbidden inside it.

### `## 2. Allocated Responsibilities`
The black-box functional and security obligations allocated to this deployable from `system-requirements` and `risk-register`.

### `## 3. Public Contract Invariants`
The semantic guarantees, thread-safety expectations, error/result conventions, and resource-ownership rules required across the public ports and types exposed by this deployable (IEC 62304 Clause 5.3.2).
