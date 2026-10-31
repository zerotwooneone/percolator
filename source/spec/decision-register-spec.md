# Decision Register Specification (`decision-register-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and Regulatory Basis

A **Decision Register Specification** (`decision-register`) defines the structural format, lifecycle state machine, technical trade-off evaluation, and safety impact assessment for architecturally significant design decisions across the system.

In medical device and safety-critical engineering, design controls require manufacturers to document not only the final design outputs, but also the **systematic evaluation, review, and rationale** linking design inputs to design outputs:

- **ISO 13485:2016 Clause 7.3.5 (Design and Development Review)**: Mandates formal, systematic reviews at suitable stages to evaluate whether design results satisfy requirements and to identify problems. Records of review results, participating roles, and necessary actions must be maintained in the Design History File.
- **ISO 13485:2016 Clause 7.3.9 (Control of Design and Development Changes)**: Requires that changes to design and development outputs be evaluated for their effect on constituent parts, manufacturing processes, and products already delivered. A structured decision register provides an immutable historical audit trail of why design baselines were altered.
- **ISO 13485:2016 Clause 7.3.10 (Design and Development Files - DDF)**: Mandates compiling records demonstrating conformity to design and development requirements, specifically including the evolution of architectural outputs.
- **FDA 21 CFR 820.30(e) (Design Review)** & **FDA Quality Management System Regulation (QMSR)**: Requires formal documented design reviews in the Design History File (DHF), identifying the design, review date, participants, and conclusions. Decision records capture technical consensus, design alternatives considered, and trade-off rationales.
- **IEC 62304:2006+AMD1:2015 Clause 5.3 (Software Architectural Design)**: For Class B and Class C software items, requires transforming software requirements into a documented architecture, establishing software items, interfaces, and SOUP integration. Clause 5.3.5 mandates evaluating architectural segregation effectiveness; decision records document the technical trade-offs justifying chosen segregation boundaries.
- **ISO 14971:2019 Clause 7.1 (Risk Control Option Analysis)**: Mandates that risk control options be evaluated according to priority (*Inherent safety by design* $\rightarrow$ *Protective measures* $\rightarrow$ *Information for safety*). The technical rationale for selecting or rejecting specific risk control architectures must be documented.
- **AAMI TIR45:2023 (*Guidance on the use of AGILE practices in the development of medical device software*) Section 5 & 7**: Replaces the compliance myth of "Big Design Up Front" (BDUF) with iterative, evolutionary architecture. It explicitly endorses **Architecture Decision Records (ADRs)** maintained in version control as objective evidence of continuous design reviews and evolutionary design controls.

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `DEC-<SLUG>`, governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts). Once an approved decision is committed, its identifier is immutable; superseding decisions must link to the original rather than mutating or deleting it (`Superseded by [DEC-<NEW-SLUG>](...)`).
- **Permitted Outbound QMS References:**
  - `system-requirements` (`SYS-*`) & `deployable-requirements` (`DEP-*`): Cites requirements that drove or constrained the decision.
  - `risk-register` (`HAZ-*`): Cites hazards analyzed during trade-off evaluation.
  - `soup` (`SOUP-*`): Cites third-party dependencies evaluated as part of the architectural choice.
  - `fact` (`FACT-*`): Links to external protocol specifications, standards, or platform runtime invariants.
  - Peer `decision-register` (`DEC-*`): Links to prior decisions being superseded or extended.
- **Permitted Inbound QMS References:**
  - `design` (`INV-*`, deployable `Design.md`): Cites `DEC-<SLUG>` to show concrete realization and enforcement of the architectural decision within specific deployables.
- **Prohibited Outbound References:** **MUST NOT** cite `user-needs` (`UN-*`) or `intended-use` (`IU-*`) directly (technical decisions realize engineering requirements, not stakeholder wishes).

---

## 3. Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

### 3.1 Architectural Significance Threshold
Not every coding decision belongs in a Decision Register. A decision record (`DEC-<SLUG>`) is mandatory only when an architectural choice satisfies at least one **Significance Criterion**:
1. **Multi-Deployable Impact:** Introduces patterns, serialization formats, or IPC protocols spanning multiple deployables or layers.
2. **Safety & Risk Boundary:** Directly implements, affects, or alters a risk control measure ([`HAZ-*`](risk-register-spec.md)) or architectural segregation boundary (IEC 62304 Clause 5.3.5).
3. **Core Technology / SOUP Selection:** Introduces, replaces, or wraps a foundational SOUP dependency ([`SOUP-*`](soup-spec.md)), database engine, cryptographic primitive, or communication transport.
4. **Structural Non-Reversibility:** A high-cost choice that is difficult, costly, or risky to reverse later (e.g., synchronous RPC vs. asynchronous event mesh, relational vs. document storage, zero-knowledge proofs vs. server validation).

### 3.2 Division of Responsibility with Deployable Design Specifications (`design-spec`)
To prevent duplicate decision-making and prefix collision:
- **`decision-register` (`DEC-*`)**: Owns system-wide, cross-cutting architectural baselines, protocol selections, and multi-deployable trade-offs.
- **`design-spec` (`INV-*`, local `DEC-*`)**: Owns deployable-internal architectural invariants and localized technical mechanisms. Deployable `Design.md` files implement system decisions by linking inline (`[DEC-<SLUG>]`), never redefining them.

### 3.3 Formal Lifecycle Status Transitions and Review Evidence
Every `DEC-<SLUG>` item must maintain an explicit status and formal review record satisfying FDA 21 CFR 820.30(e) and ISO 13485 Clause 7.3.5:
- `Proposed`: Under active engineering and regulatory review; not yet baselined for production code.
- `Approved`: Formally reviewed and accepted as the authoritative architectural baseline (recording date and reviewing role).
- `Implemented`: Verified as fully realized in the codebase by downstream deployables.
- `Superseded`: Replaced by a subsequent decision (must include an explicit markdown link: `Superseded by [DEC-<NEW-SLUG>](...)`).
- `Deprecated`: Retired without direct replacement due to obsolescence or requirement removal.

### 3.4 Mandatory Rationale and Rejected Alternatives (The "Why")
To satisfy FDA 21 CFR 820.30(e) design review expectations and prevent architectural regression:
- Every decision **MUST** document at least one **Rejected Alternative**.
- The rationale for rejection must state the technical, safety, performance, or regulatory grounds on which the alternative was disqualified (e.g., unmitigated hazard, GC heap fragmentation, memory safety vulnerability, platform incompatibility).

### 3.5 ISO 14971 Risk Control Option Analysis Alignment
When an architectural decision implements or affects a hazard mitigation ([`HAZ-*`](risk-register-spec.md)):
- The entry **MUST** evaluate the decision against the ISO 14971 Clause 7.1 control priority order (*Inherent safety by design* $\rightarrow$ *Protective measures* $\rightarrow$ *Information for safety*).
- If a protective control was chosen instead of an inherently safe design, the entry must document the technical or operational feasibility justification.

### 3.6 Document Granularity (Consolidated Register vs. Granular Records)
To balance ease of browsing against Git merge contention in active teams:
- **Consolidated Register (`DecisionRegister.md`)**: Groups all active system decisions into a single top-level register. Recommended for compact systems.
- **Granular Decision Records (`decisions/DEC-<SLUG>.md`)**: Places each decision in a dedicated markdown document under a `decisions/` directory. Permitted for larger teams to avoid Git merge contention across concurrent feature branches.

### 3.7 Strict Anti-Duplication & Exclusions
1. **Never Duplicate Requirement or Hazard Prose:** In the *Context & Problem Statement*, never copy-paste the text of a requirement or hazard. Simply cite `[SYS-<SLUG>]` or `[HAZ-<SLUG>]` and describe strictly the technical friction, conflicting constraints, or trade-offs that forced the decision.
2. **Routine Implementation Details:** Local variable naming, internal helper method refactoring, or routine bug fixes do not belong in a Decision Register.
3. **Tunable Constants & Code Inventories:** Numeric buffer sizes, retry timeouts, or class rosters belong in code (governed by [Code-as-Truth](qms-spec.md#5-universal-anti-duplication--stability-rules)).
4. **Clinical / User Need Narratives:** Clinical justifications belong in `intended-use-spec` and `user-needs-spec`.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these instructional explanations:

### `# <System Name> Decision Register`
`Spec: [decision-register-spec](<relative-path>/spec/decision-register-spec.md)`

### `## 1. Architectural and Structural Decisions`
Decisions governing subsystem decomposition, layer boundaries, IPC/network topology, or data storage foundations.

Each entry **MUST** use the following atomic format:

1. **`DEC-<SLUG>` (<Title>):**
   - **Status & Review Record:** `Approved (YYYY-MM-DD | Reviewer/Role)` | `Proposed` | `Implemented` | `Superseded by [DEC-<SLUG>](...)` | `Deprecated`
   - **Context & Problem Statement:** The technical, regulatory, or environmental forces driving the decision, citing driven requirements ([`SYS-*`](system-requirements-spec.md), [`DEP-*`](deployable-requirements-spec.md)) and platform runtime facts ([`FACT-*`](fact-spec.md)) without repeating their prose.
   - **Decision & Technical Rationale:** The selected architectural path and the core technical justification for its selection.
   - **Rejected Alternatives:** Specific competing patterns or technologies evaluated and the technical or safety reasons for their rejection.
   - **Safety, Risk, and Security Impact:** Assessment of how the decision interacts with risk controls ([`HAZ-*`](risk-register-spec.md)), declaring its ISO 14971 control priority (*Inherent* vs. *Protective*), third-party SOUP items ([`SOUP-*`](soup-spec.md)), or IEC 62304 segregation boundaries.

### `## 2. Cryptographic, Protocol, and Security Decisions`
Decisions governing identity establishment, zero-knowledge proofs, end-to-end wire encryption, sealed sender routing, or local database vault protection, formatted identically to Section 1.

### `## 3. Integration, Transport, and Platform Decisions`
Decisions governing operating system interop, runtime bindings, background daemon lifecycle, or hardware platform prerequisites, formatted identically to Section 1.
