# System Requirements Specification (`system-requirements-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **System Requirements Specification** (`system-requirements`) document defines the black-box **Design Inputs** for the integrated software system. It translates stakeholder `user-needs` (`UN-*`) and `risk-register` control measures (`RCM-*`) into objectively verifiable system-level obligations without dictating internal assembly boundaries, class structures, or database schemas (**IEC 62304 Clause 5.2**, **ISO 13485 Clause 7.3.3 & 7.3.6**, **FDA 21 CFR 820.30(c) & (f)**).

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `SYS-<SLUG>`, tagged with a verification method (`[Verify: Unit | Integration | Simulation | StaticAnalysis]`) per [`qms-spec`](qms-spec.md#33-lightweight-verification-method-tags-verify-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts).
- **Permitted Outbound QMS References:** `user-needs` (`UN-*`), `risk-register` (`RCM-*`), and `fact` (`FACT-*`) documents only.
- **Prohibited Outbound References:** **MUST NOT** reference `intended-use` directly (traces through `user-needs` and `risk-register`) and **MUST NOT** reference downstream `deployable-requirements` or `design` documents.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Black-Box Design Verification Boundary (System "What", Never Internal "How"):** Every `SYS-<SLUG>` must be objectively testable at the system boundary via Design Verification without inspecting internal code structure:
   - *Include:* System-level functional obligations realizing `UN-*` (e.g., store-and-forward relay buffering for offline peers, multi-device state synchronization) and verifiable security/privacy invariants realizing `RCM-*` controls (e.g., forward secrecy, post-compromise recovery, group epoch rotation upon member removal, wire-level indistinguishability of blocked vs. offline peers, and at-rest vault encryption).
   - *Exclude Internal Assembly / Code Architecture:* Never mention internal project/assembly names (`Percolator.Domain`, `Percolator.Infrastructure`), C# interfaces, SQL tables, or UI framework mechanics (`Avalonia`, `ViewModels`), which belong in `deployable-requirements` and `design`.
2. **Essential Verification Baseline Only (No "Nice-to-Haves"):** Exclude optional convenience features or experimental plugins that are not required to satisfy `UN-*` or `RCM-*`.
3. **Atomic Obligations & Inline Traceability:** Each `SYS-<SLUG>` must state a single verifiable obligation and link inline to the upstream `UN-<SLUG>` and/or `RCM-<SLUG>` it realizes without repeating the upstream narrative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> System Requirements`
`Spec: [system-requirements-spec](<relative-path>/spec/system-requirements-spec.md)`

### `## 1. Functional System Capabilities`
Atomic, black-box functional obligations (`SYS-<SLUG>`) of the system as a whole that realize `UN-*` across identity lifecycle, direct/group conversations, and asynchronous/relayed delivery.

### `## 2. Security, Privacy, and Risk Control Invariants`
Atomic, system-wide cryptographic, metadata-privacy, network-stealth, and data-at-rest invariants (`SYS-<SLUG>`) that realize `RCM-*` controls from the `risk-register`.

### `## 3. External Interfaces and Resource Boundaries`
Black-box peer/relay trust boundaries, wire interoperability obligations, and resource-protection envelopes (`SYS-<SLUG>`, IEC 62304 Clause 5.2.2(a)–(c)).
