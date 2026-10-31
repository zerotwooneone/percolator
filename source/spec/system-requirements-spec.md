# System Requirements Specification (`system-requirements-spec`)

## 1. Purpose and QMS Basis

A **System Requirements Specification** (`system-requirements`) document defines the black-box **Design Inputs** for the integrated software system. It translates stakeholder `user-needs` and `risk-register` control measures (`RCM-*`) into verifiable system-level obligations without dictating internal assembly boundaries, class structures, or database tables.

- **IEC 62304:2006+AMD1:2015 Clause 5.2** (*Software requirements analysis*): Specifying system functional capabilities, external interfaces, security invariants, and risk control measures re-expressed as software requirements.
- **ISO 13485:2016 Clause 7.3.3** & **FDA 21 CFR 820.30(c)**: Ensuring design inputs are unambiguous, verifiable, and non-conflicting.

---

## 2. Dependency Rules

- **Permitted Outbound QMS References:** `user-needs`, `risk-register` (`RCM-*`), and `fact` documents only (linked inline where a requirement realizes a specific user need, risk control, or external standard).
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` directly (traces through `user-needs` and `risk-register`) and **MUST NOT** reference downstream `deployable-requirements` or `design` documents.

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [system-requirements-spec](<relative-path>/spec/system-requirements-spec.md)`
2. **System-Boundary Invariants Over Feature Micro-Specs:** Specify enduring black-box system guarantees (e.g., end-to-end encryption properties, zero-metadata relay routing, vault encryption at rest, dual-plane bulk transfer separation). Do not document transient UI layouts, CLI flags, or internal wire field numbers that change during routine feature iterations.
3. **Reference Risk Controls Instead of Restating Threats:** When a system requirement implements a Risk Control Measure, link to the `RCM-<SLUG>` in `risk-register` rather than re-explaining the threat scenario.
4. **No Boilerplate Traceability Tables:** Do not maintain separate "Upstream References" link-dump sections; use concise inline links where traceability adds context.
5. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> System Requirements`
`Spec: [system-requirements-spec](<relative-path>/spec/system-requirements-spec.md)`

### `## 1. Functional System Capabilities`
Black-box functional obligations of the system as a whole across its primary problem domains (identity, messaging, discovery, file transfer).

### `## 2. Security, Privacy, and Risk Control Invariants`
System-wide security, cryptographic isolation, network stealth, and data-at-rest requirements that realize `RCM-*` controls from the `risk-register`.

### `## 3. External Interfaces and Resource Boundaries`
Black-box wire interoperability boundaries, untrusted peer/relay trust boundaries, and high-level capacity/resource envelopes (IEC 62304 Clause 5.2.2(a)–(c)).
