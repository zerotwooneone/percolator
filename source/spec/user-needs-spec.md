# User Needs Specification (`user-needs-spec`)

## 1. Purpose and QMS Basis

A **User Needs Specification** (`user-needs`) document captures high-level stakeholder capabilities and trust expectations in the problem domain ("voice of the user"). In the QMS V-Model, User Needs translate the `intended-use` charter into stakeholder outcomes that drive `system-requirements` (Design Inputs) and serve as the acceptance baseline for Design Validation.

- **FDA 21 CFR 820.30(c) & 820.30(g)** (*Design input & Design validation*): Ensuring design inputs address user needs and validating that the finished system conforms to those needs.
- **ISO 13485:2016 Clause 7.3.3 & 7.3.7**: Capturing stakeholder functional, usability, and safety expectations prior to engineering design.
- **IEC 62304:2006+AMD1:2015 Annex B.5.2**: Separating problem-domain stakeholder needs from verifiable system requirements.

---

## 2. Dependency Rules

- **Permitted Outbound QMS References:** `intended-use` and `fact` documents only (linked inline where relevant).
- **Prohibited Outbound References:** **MUST NOT** reference `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [user-needs-spec](<relative-path>/spec/user-needs-spec.md)`
2. **Outcome-Level Granularity (Avoid UI/Feature Micro-Specs):** State enduring user outcomes (e.g., *"Communicate privately with individuals and groups without relying on a central server or leaking social graphs"*), **not** individual UI buttons, dialog flows, or protocol mechanics. Adding a minor UI enhancement or fixing a bug should never require editing `user-needs`.
3. **Do Not Repeat Personas or Threat Environments:** Reference the user profiles and operating environment in `intended-use` rather than restating them.
4. **No Inline Traceability Matrices:** Do not maintain brittle Upstream/Downstream link tables at the top or bottom of the file; link inline to `intended-use` only when a need is specific to a single user profile or environment boundary.
5. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> User Needs`
`Spec: [user-needs-spec](<relative-path>/spec/user-needs-spec.md)`

### `## 1. Core Functional Needs`
Concise problem-domain statements of the primary capabilities users need the system to perform (e.g., sovereign identity management, asynchronous messaging, group collaboration, bulk file sharing, opt-in discovery).

### `## 2. Sovereignty, Privacy, and Operational Trust Needs`
Stakeholder expectations regarding data ownership, zero-metadata leakage, coercion/duress resilience, offline/degraded network operation, and explicit user consent before network exposure or resource consumption.
