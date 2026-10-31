# User Needs Specification (`user-needs-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **User Needs Specification** (`user-needs`) document captures essential stakeholder capabilities and usability/operational expectations in the problem domain ("voice of the user"). In the QMS V-Model, User Needs translate the `intended-use` charter into observable user outcomes that drive `system-requirements` (Design Inputs) and serve as the mandatory acceptance baseline for Design Validation (**FDA 21 CFR 820.30(c) & (g)**, **ISO 13485:2016 Clause 7.3.2 & 7.3.7**, **IEC 62304 Annex B.5.2**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `UN-<SLUG>` (governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts); no deletion, renaming, or weakening without human approval).
- **Permitted Outbound QMS References:** `intended-use` (`IU-*`, `USR-*`, `ENV-*`, `CONTRA-*`) and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** reference `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Solution-Free "Voice of the User" (What, Never How):** Every `UN-<SLUG>` must state *what* the user needs to accomplish or experience in the problem domain, **never** the internal architectural or engineering mechanism used to achieve it.
2. **Simulated-Use Validatability (Design Validation vs. Design Verification):** Under **FDA 21 CFR 820.30(g)**, every User Need must be directly testable by observing a representative user operating the finished product in simulated or actual use. **Exclude** properties that require internal protocol inspection, mathematical proofs, or low-level adversarial/fault injection to verify:
   - *Exclude Technical & Protocol Invariants:* Low-level cryptographic properties, state-machine recovery invariants, or wire-level indistinguishability belong in `risk-register` and `system-requirements`.
   - *Exclude Protective Warnings / Risk Controls:* Specific hazard/threat alerts are *Information for Safety / Protective Risk Control Measures* (`RCM-*`) in `risk-register`, not primary User Needs.
3. **Essential Validation Baseline Only (No "Nice-to-Haves"):** Because failing any single `UN-<SLUG>` means the product fails Design Validation, every listed need must be **essential**—if the capability were absent, the system would fail its `intended-use`. Optional conveniences, secondary workflows, or "nice-to-have" features **MUST** be excluded.
4. **Atomic Needs & Zero Charter Duplication:** Each `UN-<SLUG>` must describe one cohesive user capability without repeating charter definitions (`IU-*`), user profiles (`USR-*`), or environment assumptions (`ENV-*`) from `intended-use`.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> User Needs`
`Spec: [user-needs-spec](<relative-path>/spec/user-needs-spec.md)`

### `## 1. Core Functional Needs`
Atomic, solution-free statements (`UN-<SLUG>`) of the essential functional capabilities users must be able to perform in simulated-use validation for the system to fulfill its intended use.

### `## 2. Sovereignty, Privacy, and Operational Trust Needs`
Atomic, user-observable expectations (`UN-<SLUG>`) regarding operational trust, privacy/safety controls, session lifecycle control, data retention, and resource boundaries.
