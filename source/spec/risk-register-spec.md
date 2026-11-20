# Risk Register Specification (`risk-register-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Risk Register Specification** (`risk-register`) document identifies systemic hazards, threat actors, and failure modes (`HAZ-<SLUG>`) arising from the system's `intended-use` and evaluates their initial and residual risk (**ISO 14971:2019 Clauses 5–8**, **IEC 62304 Clauses 7.1–7.3**, **ISO 13485 Clause 7.3.3(c)**, **AAMI TIR57**).

To prevent 1-to-1 text duplication between the Risk Register and System Requirements, the `risk-register` defines **only the hazards (`HAZ-<SLUG>`)**, while the normative Risk Control Measures are defined once as verifiable `SYS-<SLUG>` obligations in `system-requirements` that link upward to the `HAZ-<SLUG>` they mitigate.

---

## 2. Dependency & Identification Rules (Acyclic Hazard Direction)

- **Item Identifier Prefix:** `HAZ-<SLUG>` (governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts); no deletion, renaming, or weakening without human approval).
- **Mandatory Human Approval for Risk & Harm Calculations:** Any modification to a hazard's **Harm definition**, **Severity (`S`)**, **Probability/Likelihood (`L`)**, **initial or residual risk calculation**, **control status**, or the **scoring rubric** in Section 3.4 requires explicit human approval (**ISO 14971 Clauses 4.4, 7.3, & 8**).
- **Permitted Outbound QMS References:** `intended-use` (`IU-*`, `USR-*`, `ENV-*`, `CONTRA-*`), `user-needs` (`UN-*`), and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** link downward to `system-requirements`, `deployable-requirements`, or `design` files. Downstream `SYS-*` requirements link *upward* to `HAZ-<SLUG>`.

---

## 3. Document-Specific Inclusion, Exclusion, & Scoring Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Systemic Hazards Only (Zero Duplication of Risk Control Requirements):**
   - *Include:* Architectural-level safety, security, privacy, and availability hazards (`HAZ-<SLUG>`), their environmental/adversarial cause, the resulting hazardous situation and harm, and their initial/residual risk scores.
   - *Exclude Separate Risk Control Measure (`RCM`) Lists:* Do **not** write a second section defining Risk Control Measures in the Risk Register, and do **not** write requirement prose (`must`/`shall`) inside residual risk justifications. Normative risk controls are defined **once** as `SYS-<SLUG>` items in `system-requirements`.
   - *Exclude Routine Bugs & Design Mechanics:* Do not add entries for ordinary coding defects, and do not name specific internal algorithms or implementation mechanics (e.g., specific key-exchange curves, bucket padding schemes, or OS credential APIs) inside a hazard description.
2. **Atomic Hazards (Never Bundle Distinct Threat Models):** Each `HAZ-<SLUG>` must represent a single failure mode or threat vector with a uniform initial risk score. Never bundle distinct threats (e.g., offline disk inspection vs. active user coercion, or pre-unlock network exposure vs. post-lock memory residue) into one compound hazard.
3. **ISO 14971 Severity vs. Likelihood Rule (Human-Approved Changes Only):**
   - **Severity (`S`)** measures the worst-case harm *if the hazard still occurs*; **Likelihood (`L`)** measures the probability or feasibility of the harm occurring.
   - **Protective and Information controls reduce Likelihood (`L`), NOT Severity (`S`).** Residual Severity (`S`) may only be lower than Initial Severity when an **Inherent** design change structurally bounds the maximum possible blast radius of the harm (e.g., bounding a state compromise to a single transient message rather than permanent identity compromise).
   - No AI agent or automated process may alter any `S`, `L`, harm statement, or risk calculation—or propose a new hazard score as finalized—without explicit human approval.
4. **Standardized Scoring Rubric (Defined Once Here; Human Approval Required to Modify):** Implementing documents use these codes without redefining them:
   - **Severity:** `S4 (Catastrophic: irreversible identity compromise / RCE / permanent data loss)`, `S3 (Critical: session or social-graph exposure / persistent DoS)`, `S2 (Moderate: transient DoS / resource exhaustion / single-operation failure)`, `S1 (Minor: degraded UX with auto-recovery)`.
   - **Likelihood:** `L3 (High: unauthenticated remote attacker or routine network/process fault)`, `L2 (Medium: authenticated peer, untrusted intermediary, or foreseeable user error)`, `L1 (Low: requires local physical/OS access or computationally infeasible attack)`.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> Risk Register`
`Spec: [risk-register-spec](<relative-path>/spec/risk-register-spec.md)`

### `## 1. Cryptographic, Identity, and Privacy Hazards`
Atomic hazards (`HAZ-<SLUG>`) threatening confidentiality, authenticity, trust verification, or metadata privacy. Each entry states: the threat cause (referencing `ENV-*` / `USR-*`), the hazardous situation & harm (referencing `IU-*` / `UN-*`), Initial Risk (`S`/`L`), and Residual Risk (`S`/`L` and status: `Identified` | `Controlled` | `Verified`).

### `## 2. Endpoint, Availability, and Integrity Hazards`
Atomic hazards (`HAZ-<SLUG>`) threatening local data at rest, session lifecycle isolation, resource availability (DoS), or crash-recovery state integrity, using the same entry format as Section 1.
