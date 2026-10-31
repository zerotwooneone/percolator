# Design Specification (`design-spec`)

## 1. Purpose and QMS Basis

A **Design Specification** (`design`) document—placed as `Design.md` at the root of a deployable project—is the high-signal architectural guide (**Software Design Output**) for that deployable (IEC 62304 *Software Item*). Its purpose is to give engineers and AI assistants immediate context on the deployable's layer boundary, internal invariants, and non-obvious technical design choices **without having to read every source file** and **without breaking every time a feature is added or a bug is fixed**.

- **IEC 62304:2006+AMD1:2015 Clause 5.3 & Clause 5.4** (*Software architectural & detailed design*): Documenting Software Item structure, encapsulation boundaries, state/concurrency models, and risk control realizations.
- **ISO 13485:2016 Clause 7.3.4** & **FDA 21 CFR 820.30(d)** (*Design output*): Documenting the engineering design that realizes design inputs.
- **ISO 14971:2019 Clause 7.1 & 7.2**: Documenting how inherently safe design and protective risk controls are realized in architecture, including rationale over rejected alternatives.

---

## 2. Dependency Rules

- **Permitted Outbound QMS References:** Its corresponding `deployable-requirements` (or `system-requirements`), `risk-register`, `fact`, and **inward `design` documents** strictly matching compile-time project references.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` or `user-needs` directly, and **MUST NOT** link to outward or peer `design` documents that this project does not depend on at compile time.

---

## 3. Anti-Duplication & Anti-Brittleness Rules

To keep `Design.md` files concise, high-signal, and immune to routine code churn, implementing documents **MUST** obey these rules:

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [design-spec](<relative-path>/spec/design-spec.md)`
2. **Never Duplicate `.csproj` or the Filesystem (Code-as-Truth):**
   - **DO NOT** list NuGet packages, framework versions, or `<ProjectReference>` lists (`Permitted Inward References` link dumps); `.csproj` is already the single source of truth for build dependencies.
   - **DO NOT** include directory trees, file lists, or exhaustive inventories of every class, slice, repository, or wire message type; the filesystem and IDE symbol index already provide that.
3. **Never Duplicate Constants or Feature Catalogues (Zero Feature-Churn Brittleness):**
   - **DO NOT** hardcode tunable numeric constants (e.g., exact Argon2 memory sizes, keep-alive seconds, window sizes, byte bucket tiers, or repository counts) that live in code or `README.md`. Describe the *mechanism and invariant* (e.g., *stepped bucket padding prior to AEAD encryption*), not the magic numbers.
4. **Zero Intra-Document Repetition Across Sections:**
   - Each of the 3 allowed sections has a distinct job. If a concept is listed as an In-Scope responsibility in Section 1, do not re-list it as a folder/module in Section 2 and re-explain it in Section 3.
5. **Define Shared Rules Once in the Innermost Deployable (DRY Inheritance):**
   - Cross-cutting rules (such as `[ByteArray]`/`[GuidId]` primitive-obsession guards in `Percolator.SourceGenerators`, `DomainResult<T>` and `IDateTimeProvider` in `Percolator.Domain.Kernel`, or `internal` aggregate rehydration in `Percolator.Domain`) **MUST** be defined **once** in the innermost deployable's `Design.md`. Downstream `Design.md` files may link inline to that inward `Design.md` when extending the pattern, never restating it.
6. **History Policy (Git vs. Rejected Alternatives Exception):**
   - Prose edit history tables are prohibited (`git log` is authoritative).
   - **QMS Exception (ISO 14971 Clause 7.1):** Under Section 3 (*Key Technical Design Choices*), briefly noting a **Rejected Alternative** alongside a non-obvious design choice is encouraged so engineers and AI agents do not re-introduce a rejected anti-pattern.

---

## 4. Allowed Headings and Section Definitions

An implementing `Design.md` **MUST** use only the three H2 headings below in this exact order and **MUST NOT** repeat these section explanations:

### `# <Deployable Name>`
`Spec: [design-spec](<relative-path>/spec/design-spec.md)`

### `## 1. Purpose and Layer Boundary`
States why this deployable exists as a distinct assembly in the Onion Architecture, what responsibilities belong inside it (**In Scope**), and what responsibilities are strictly excluded to inward or outward layers (**Out of Scope** segregation boundary per IEC 62304 Clause 5.3.4).

### `## 2. Architectural Invariants and Encapsulation`
Defines the timeless structural, visibility, concurrency, and state-ownership rules that all code inside this deployable must obey (e.g., aggregate mutation/rehydration visibility, port purity, thread-safety gates, memory zeroization), linking inline to an inward `Design.md` for inherited kernel rules rather than repeating them.

### `## 3. Key Technical Design Choices`
Records the non-obvious architectural mechanisms and trade-offs chosen to solve core problems in this deployable—along with brief *Rejected Alternatives* where prevents regression—without repeating Section 1/2 or hardcoding tunable code constants.
