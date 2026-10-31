# System Requirements Specification (`system-requirements-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **System Requirements Specification** (`system-requirements`) document defines the black-box **Design Inputs** and **Risk Control Measures** for the integrated software system. It translates stakeholder `user-needs` (`UN-*`), `risk-register` hazards (`HAZ-*`), and external `fact` constraints (`FACT-*`) into objectively verifiable system-level obligations without dictating internal assembly boundaries, class structures, or database schemas (**IEC 62304 Clauses 5.2 & 7.2**, **ISO 13485 Clauses 7.3.3 & 7.3.6**, **ISO 14971 Clause 7.1**, **FDA 21 CFR 820.30(c) & (f)**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `SYS-<SLUG>`, tagged with a system-boundary verification method (`[Verify: Integration | Simulation]`)—plus `[Control: Inherent | Protective | Information]` whenever the requirement mitigates a `HAZ-*` hazard—per [`qms-spec`](qms-spec.md#33-lightweight-verification--risk-control-tags-verify--control-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts).
- **Permitted Outbound QMS References:** `user-needs` (`UN-*`), `risk-register` (`HAZ-*`), and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` directly (traces through `user-needs` and `risk-register`) and **MUST NOT** reference downstream `deployable-requirements` or `design` documents.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Product Black-Box Boundary (System "What", Never Internal "How"):** Every `SYS-<SLUG>` specifies an obligation of the integrated product black box observable at its external user/OS boundary or external communication boundary via Design Verification:
   - *Include:* System-level functional obligations realizing `UN-*`, verifiable security/safety/resilience Risk Control Measures mitigating `HAZ-*`, and external interface/resource-boundary obligations.
   - *Exclude Internal Assembly / Code Architecture:* Never mention internal project/assembly names, source code interfaces, database table schemas, UI framework classes, or specific internal design mechanisms (e.g., stepped bucket padding tiers or OS keyring implementation details), which belong in `deployable-requirements` and `design`.
   - *Exclude Non-Product Verification Tooling:* Standalone test or simulation harnesses are external verification test equipment (**ISO 13485 Clause 7.5.6** / **FDA 21 CFR 820.70(i)**), not part of the product system under specification.
2. **Legitimate Upstream Backing (No Unbacked / Reverse-Engineered Requirements):** Every `SYS-<SLUG>` **MUST** be justified by at least one of the three QMS Design Input sources (**ISO 13485 Clause 7.3.3**):
   - A stakeholder User Need (`UN-*`),
   - An identified Hazard (`HAZ-*`) in the `risk-register`, or
   - A mandatory external interface, regulatory, or platform constraint (`FACT-*` / **IEC 62304 Clause 5.2.2(a)–(c)**).
   Never reverse-engineer optional code features into `system-requirements` if they are not required by `UN-*`, `HAZ-*`, or `FACT-*`.
3. **Atomic Obligations & Single-Place Definition:** Each `SYS-<SLUG>` must state a single verifiable obligation in exactly one section (if a requirement both realizes a `UN-*` and mitigates a `HAZ-*`, state it once and cite both inline).

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> System Requirements`
`Spec: [system-requirements-spec](<relative-path>/spec/system-requirements-spec.md)`

### `## 1. Functional System Capabilities`
Atomic, black-box functional obligations (`SYS-<SLUG>`) of the integrated system that realize `UN-*` (and cite any `HAZ-*` they directly mitigate).

### `## 2. Security, Privacy, and Risk Control Invariants`
Atomic, system-wide security, safety, privacy, and resilience Risk Control Measures (`SYS-<SLUG>`) that mitigate `HAZ-*` hazards from the `risk-register`.

### `## 3. External Interfaces and Resource Boundaries`
Black-box external interface protocols, trust boundaries, and resource-protection envelopes (`SYS-<SLUG>`, IEC 62304 Clause 5.2.2(a)–(c)).
