# System Requirements Specification (`system-requirements-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **System Requirements Specification** (`system-requirements`) document defines the black-box **Design Inputs** for the integrated software system. It translates stakeholder `user-needs` (`UN-*`), `risk-register` control measures (`RCM-*`), and external `fact` constraints (`FACT-*`) into objectively verifiable system-level obligations without dictating internal assembly boundaries, class structures, or database schemas (**IEC 62304 Clause 5.2**, **ISO 13485 Clause 7.3.3 & 7.3.6**, **FDA 21 CFR 820.30(c) & (f)**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `SYS-<SLUG>`, tagged with a system-boundary verification method (`[Verify: Integration | Simulation]`) per [`qms-spec`](qms-spec.md#33-lightweight-verification-method-tags-verify-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts).
- **Permitted Outbound QMS References:** `user-needs` (`UN-*`), `risk-register` (`RCM-*`), and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` directly (traces through `user-needs` and `risk-register`) and **MUST NOT** reference downstream `deployable-requirements` or `design` documents.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Product Black-Box Boundary (System "What", Never Internal "How"):** Every `SYS-<SLUG>` specifies an obligation of the integrated product black box observable at its external user/OS boundary or external communication boundary via Design Verification:
   - *Include:* System-level functional obligations realizing `UN-*`, verifiable security/safety/resilience invariants realizing `RCM-*` controls, and external interface/resource-boundary obligations.
   - *Exclude Internal Assembly / Code Architecture:* Never mention internal project/assembly names, source code interfaces, database table schemas, or UI framework classes, which belong in `deployable-requirements` and `design`.
   - *Exclude Non-Product Verification Tooling:* Standalone test or simulation harnesses are external verification test equipment (**ISO 13485 Clause 7.5.6** / **FDA 21 CFR 820.70(i)**), not part of the product system under specification.
2. **Legitimate Upstream Backing (No Unbacked / "Gold-Plated" Requirements):** Every `SYS-<SLUG>` **MUST** be justified by at least one of the three QMS Design Input sources (**ISO 13485 Clause 7.3.3**):
   - A stakeholder User Need (`UN-*`),
   - A Risk Control Measure (`RCM-*`) mitigating an identified hazard, or
   - A mandatory external interface, regulatory, or platform constraint (`FACT-*` / **IEC 62304 Clause 5.2.2(a)–(c)**).
   Requirements that do not trace to any of these three sources are prohibited as unbacked scope creep.
3. **Atomic Obligations & Inline Traceability:** Each `SYS-<SLUG>` must state a single verifiable obligation and link inline to the upstream `UN-<SLUG>`, `RCM-<SLUG>`, and/or `FACT-<SLUG>` it realizes without repeating the upstream narrative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> System Requirements`
`Spec: [system-requirements-spec](<relative-path>/spec/system-requirements-spec.md)`

### `## 1. Functional System Capabilities`
Atomic, black-box functional obligations (`SYS-<SLUG>`) of the integrated system that realize `UN-*`.

### `## 2. Security, Privacy, and Risk Control Invariants`
Atomic, system-wide security, safety, privacy, and resilience invariants (`SYS-<SLUG>`) that realize `RCM-*` controls from the `risk-register`.

### `## 3. External Interfaces and Resource Boundaries`
Black-box external interface protocols, trust boundaries, and resource-protection envelopes (`SYS-<SLUG>`, IEC 62304 Clause 5.2.2(a)–(c)).
