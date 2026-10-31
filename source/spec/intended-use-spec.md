# Intended Use Specification (`intended-use-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

An **Intended Use Specification** (`intended-use`) document is the foundational charter of the system. It defines *why* the system exists, *who* operates it, *what environment* it operates within, and *what uses are out of scope or contraindicated* (**ISO 14971:2019 Clause 5.2**, **ISO 13485:2016 Clause 7.3.2**, **FDA 21 CFR 801.4**, **IEC 62304 Clause 4.3**).

---

## 2. Dependency & Identification Rules (Root Charter Node)

- **Item Identifier Prefixes:**
  - `IU-<SLUG>` for core intended purpose guarantees (Section 1)
  - `USR-<SLUG>` for target user profiles (Section 2)
  - `ENV-<SLUG>` for operating environment boundaries (Section 3)
  - `CONTRA-<SLUG>` for contraindications and out-of-scope uses (Section 4)
  - All identifiers are governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts) (no deletion, renaming, or weakening without human approval).
- **Permitted Outbound QMS References:** `fact` documents only (`FACT-*`).
- **Prohibited Outbound References:** **MUST NOT** reference `user-needs`, `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Charter-Level Stability (No Implementation Reverse-Engineering):** Define only the enduring problem domain, external operator classes, and trust/safety boundary—**never** what happens to be implemented today:
   - *Include:* High-level system purpose (`IU-*`), external user/operator profiles (`USR-*`), physical/network/host operating environment (`ENV-*`), and explicit contraindications/non-goals (`CONTRA-*`).
   - *Exclude Specific Features & UX Flows:* Granular feature catalogs, UI screens, or workflow sequences.
   - *Exclude Technical & Protocol Mechanisms:* Internal algorithms, specific cryptographic primitives, or wire framing protocols (which belong in `risk-register`, `system-requirements`, or `design`).
   - *Exclude Non-Product Verification Tooling:* Internal engineering simulators, bench harnesses, or test runners are verification tools (**ISO 13485 Clause 7.5.6**), not intended operational uses or user profiles of the product.
2. **Define Personas, Environments, and Contraindications Once:** Downstream `user-needs` and `risk-register` documents must reference `IU-*`, `USR-*`, `ENV-*`, and `CONTRA-*` IDs inline rather than re-describing user profiles or operating environments.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Intended Use`
`Spec: [intended-use-spec](<relative-path>/spec/intended-use-spec.md)`

### `## 1. Intended Purpose`
A concise, solution-agnostic statement of the core problem the system solves and the high-level safety, security, privacy, or operational guarantees (`IU-<SLUG>`) it provides.

### `## 2. Target User Profiles`
The distinct external classes of human users or operators (`USR-<SLUG>`), their skill/trust assumptions, and their operational roles.

### `## 3. Operating Environment`
The physical, network, and host environment conditions (`ENV-<SLUG>`) in which the software is designed to operate.

### `## 4. Contraindications and Out-of-Scope Uses`
Explicit scenarios, threat/hazard models, or use cases (`CONTRA-<SLUG>`) for which the system is **not** intended and must not be relied upon.
