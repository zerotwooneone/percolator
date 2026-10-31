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
2. **Charter-Level Stability (Zero Feature Churn):** This document must remain stable across individual feature additions and bug fixes. It defines the enduring problem domain and trust boundary, **never** feature catalogues, UI screens, wire protocols, or software architecture.
3. **Define Personas and Environments Once:** User profiles and operating environment assumptions are defined **once** here. Downstream `user-needs` and `risk-register` documents must reference these sections rather than re-describing who the user is or what network adversary model applies.
4. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Intended Use`
`Spec: [intended-use-spec](<relative-path>/spec/intended-use-spec.md)`

### `## 1. Intended Purpose`
A concise statement of the core problem the system solves and the primary security, privacy, or operational guarantee it provides.

### `## 2. Target User Profiles`
The distinct classes of human users or node operators, their technical/trust assumptions, and their operational roles.

### `## 3. Operating Environment`
The physical, network, and host OS conditions in which the software is designed to operate (e.g., untrusted WAN/LAN networks, NAT/firewall constraints, single-user endpoints vs. untrusted relays).

### `## 4. Contraindications and Out-of-Scope Uses`
Explicit scenarios, threat models (e.g., compromised host OS kernel/hardware keyloggers), or use cases for which the system is **not** intended and must not be relied upon.
