# Intended Use Specification (`intended-use-spec`)

## 1. Purpose and QMS Basis

An **Intended Use Specification** (`intended-use`) document is the foundational charter of the system. It defines *why* the system exists, *who* operates it, *what environment* it operates within, and *what uses are out of scope or contraindicated*—giving human engineers and AI assistants immediate high-level context before reading requirements or code.

- **ISO 14971:2019 Clause 5.2**: Documenting intended use, user profiles, operating environment, and reasonably foreseeable misuse as the root input to hazard analysis.
- **ISO 13485:2016 Clause 7.3.2 & 7.3.3** & **FDA 21 CFR 820.30(c) / 801.4**: Establishing the system charter against which design validation is performed.
- **IEC 62304:2006+AMD1:2015 Clause 4.3**: Deriving system safety/security criticality from intended use.

---

## 2. Dependency Rules (Root Charter Node)

An `intended-use` document sits at the **Root Charter Layer** of the QMS Directed Acyclic Graph (DAG):

- **Permitted Outbound QMS References:** `fact` documents only (inline, when an environmental boundary depends on an external fact).
- **Prohibited Outbound References:** **MUST NOT** reference `user-needs`, `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** The document must reference this spec once immediately below the H1 title:
   `Spec: [intended-use-spec](<relative-path>/spec/intended-use-spec.md)`
2. **Charter-Level Stability (No Implementation Reverse-Engineering):** This document defines the enduring problem domain and trust boundary, **never** what happens to be implemented today. It must **not** mention:
   - Specific features or UX flows (e.g., disappearing messages, two-stage unlock screens, multi-persona switchers, safety-number dialogs).
   - Cryptographic or protocol mechanisms (e.g., forward secrecy, post-compromise security, group epochs, blinded tokens, message padding, TCP/UDP framing), which belong in `system-requirements` or `design`.
   - Internal engineering or verification harnesses (e.g., simulators or test runners), which are verification tools rather than intended operational uses of the product.
3. **Define Personas and Environments Once:** External user profiles and operating environment assumptions are defined **once** here. Downstream `user-needs` and `risk-register` documents must reference these sections rather than re-describing who the user is or what network adversary model applies.
4. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Intended Use`
`Spec: [intended-use-spec](<relative-path>/spec/intended-use-spec.md)`

### `## 1. Intended Purpose`
A concise, solution-agnostic statement of the core problem the system solves and the high-level security, privacy, or operational outcomes it provides.

### `## 2. Target User Profiles`
The distinct external classes of human users or node operators, their skill/trust assumptions, and their operational roles (excluding internal feature permissions or engineering test personas).

### `## 3. Operating Environment`
The physical, network, and host endpoint conditions in which the software is designed to operate.

### `## 4. Contraindications and Out-of-Scope Uses`
Explicit scenarios, threat models, or use cases for which the system is **not** intended and must not be relied upon.
