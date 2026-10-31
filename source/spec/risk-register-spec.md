# Risk Register Specification (`risk-register-spec`)

## 1. Purpose and QMS Basis

A **Risk Register Specification** (`risk-register`) document identifies systemic hazards, threat actors, and failure modes arising from the system's `intended-use` and defines the **Risk Control Measures (`RCM-*`)** that downstream requirements and designs must enforce.

In a Medical Device QMS, risk management is co-equal with `user-needs` as a primary driver of engineering design and architectural segregation.

- **ISO 14971:2019 Clauses 5, 6, 7, & 8** (*Risk analysis, evaluation, control, and residual risk*): Identifying hazards, estimating severity/likelihood, applying risk controls in priority order (*Inherent Design* $\rightarrow$ *Protective Measures* $\rightarrow$ *Information for Safety*), and evaluating residual risk.
- **IEC 62304:2006+AMD1:2015 Clause 7.1 – 7.3**: Identifying software hazard causes, specifying risk control measures, and enforcing architectural segregation.
- **ISO 13485:2016 Clause 7.3.3(c)** & **FDA 21 CFR 820.30(c) / AAMI TIR57**: Feeding security and safety risk controls into design inputs.

---

## 2. Dependency Rules (Acyclic Control Direction)

- **Permitted Outbound QMS References:** `intended-use`, `user-needs`, and `fact` documents only.
- **Prohibited Outbound References (Keeping the Graph Acyclic):** A `risk-register` **MUST NOT** link downward to `system-requirements`, `deployable-requirements`, or `design` files. Instead, the risk register defines concise `RCM-<SLUG>` control identifiers, and downstream requirements/design documents link *upward* to those `RCM-<SLUG>` identifiers where they are realized.

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [risk-register-spec](<relative-path>/spec/risk-register-spec.md)`
2. **Systemic Hazards Only (Not a Bug Tracker):** Record architectural-level hazards and threat classes (e.g., *Pre-Key Starvation DoS*, *Social Graph Metadata Correlation at Relays*, *Ratchet State Desynchronization on Crash*, *Forensic Key Extraction at Rest*). Do not add entries for routine coding bugs or individual CVE patches.
3. **Define Each Risk Control Measure Once:** When multiple hazards share a mitigation (e.g., *Total Network Stealth Gate* or *Bounded Quota Enforcement*), define the `RCM-<SLUG>` once in Section 2 and reference its ID from Section 1.
4. **Standardized Scoring Rubric (Defined Once Here):** Implementing documents use these codes without redefining them:
   - **Severity:** `S4 (Catastrophic: long-term identity compromise / RCE / permanent data loss)`, `S3 (Critical: session compromise / IP or social-graph deanonymization / persistent DoS)`, `S2 (Moderate: transient DoS / resource exhaustion / single-transfer failure)`, `S1 (Minor: degraded UX with auto-recovery)`.
   - **Likelihood:** `L3 (High: unauthenticated remote/relay attacker)`, `L2 (Medium: authenticated peer, malicious relay, or foreseeable user error)`, `L1 (Low: requires local OS/physical access or computationally infeasible attack)`.
   - **Control Priority (ISO 14971 7.1):** `Inherent` (eliminated structurally by design) $>$ `Protective` (guarded by runtime crypto/quotas/validation) $>$ `Information` (user warning/consent UI).
5. **History Policy (Git vs. In-File Exception):** Prose edit changelogs are prohibited (`git log` is authoritative). **QMS Exception (ISO 14971 Clauses 7.3 & 8):** Each hazard entry must include its current state (`Identified` | `Controlled` | `Verified`) and a brief residual risk justification in-file.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Risk Register`
`Spec: [risk-register-spec](<relative-path>/spec/risk-register-spec.md)`

### `## 1. Hazard and Threat Analysis`
A concise catalogue of systemic hazards (`HAZ-<SLUG>`). Each entry states: the threat/failure cause, hazardous situation & harm, initial risk (`S`/`L`), assigned `RCM-<SLUG>` control ID(s), and residual risk (`S`/`L`, status, and brief justification).

### `## 2. Risk Control Measures (`RCM`)`
The single normative definitions of each Risk Control Measure (`RCM-<SLUG>`), specifying its ISO 14971 priority (`Inherent` | `Protective` | `Information`) and the invariant barrier that downstream requirements/designs must uphold.
