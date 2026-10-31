# Risk Register Specification (`risk-register-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Risk Register Specification** (`risk-register`) document identifies systemic hazards, threat actors, and failure modes arising from the system's `intended-use` and defines the **Risk Control Measures (`RCM-*`)** that downstream requirements and designs must enforce (**ISO 14971:2019 Clauses 5–8**, **IEC 62304 Clauses 7.1–7.3**, **ISO 13485 Clause 7.3.3(c)**, **AAMI TIR57**).

---

## 2. Dependency & Identification Rules (Acyclic Control Direction)

- **Item Identifier Prefixes:**
  - `HAZ-<SLUG>` for systemic hazards and threats (Section 1)
  - `RCM-<SLUG>` for risk control measures (Section 2)
  - Governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts) (no deletion, renaming, or weakening without human approval).
- **Permitted Outbound QMS References:** `intended-use` (`IU-*`, `USR-*`, `ENV-*`, `CONTRA-*`), `user-needs` (`UN-*`), and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** link downward to `system-requirements`, `deployable-requirements`, or `design` files. Downstream documents link *upward* to `RCM-<SLUG>`.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Systemic Hazards & Barrier-Level Controls Only:**
   - *Include:* Architectural-level safety, security, privacy, and availability hazards (`HAZ-<SLUG>`) and the protective/inherent barriers (`RCM-<SLUG>`) required to mitigate them—including technical security and resilience properties excluded from `user-needs`.
   - *Exclude Routine Bugs & Code Mechanics:* Do not add entries for ordinary coding defects, compiler-enforced syntax, or specific class/database/wire-field implementation details. State the required *barrier* in `RCM-<SLUG>`, leaving internal realization to `design`.
2. **Define Each Risk Control Measure Once:** When multiple `HAZ-<SLUG>` entries share a mitigation, define the `RCM-<SLUG>` once in Section 2 and reference its ID from Section 1.
3. **Standardized Scoring Rubric (Defined Once Here):** Implementing documents use these codes without redefining them:
   - **Severity:** `S4 (Catastrophic: irreversible compromise / RCE / permanent data loss)`, `S3 (Critical: session or identity exposure / persistent DoS)`, `S2 (Moderate: transient DoS / resource exhaustion / single-operation failure)`, `S1 (Minor: degraded UX with auto-recovery)`.
   - **Likelihood:** `L3 (High: unauthenticated remote attacker or routine fault)`, `L2 (Medium: authenticated peer, untrusted intermediary, or foreseeable user error)`, `L1 (Low: requires local physical/OS access or computationally infeasible attack)`.
   - **Control Priority (ISO 14971 7.1):** `Inherent` (eliminated structurally by design) $>$ `Protective` (guarded by runtime verification/quotas/cryptography) $>$ `Information` (user warning/consent UI).

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Risk Register`
`Spec: [risk-register-spec](<relative-path>/spec/risk-register-spec.md)`

### `## 1. Hazard and Threat Analysis`
A concise catalogue of systemic hazards (`HAZ-<SLUG>`). Each entry states: the threat/failure cause (referencing `ENV-*` / `USR-*`), hazardous situation & harm, initial risk (`S`/`L`), assigned `RCM-<SLUG>` control ID(s), and residual risk (`S`/`L`, status: `Identified` | `Controlled` | `Verified`, and brief justification).

### `## 2. Risk Control Measures (`RCM`)`
The single normative definitions of each Risk Control Measure (`RCM-<SLUG>`), specifying its ISO 14971 priority (`Inherent` | `Protective` | `Information`) and the invariant barrier that downstream `system-requirements` and `design` documents must uphold.
