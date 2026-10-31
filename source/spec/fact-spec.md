# Fact Specification (`fact-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and QMS Basis

A **Fact Specification** (`fact`) document records immutable, externally verifiable truths—mathematical theorems, cryptographic primitive properties, published RFC/ISO protocol invariants, or operating system kernel behaviors—that constrain the system's design and risk analysis.

In a Medical Device QMS, separating external axioms from engineered requirements prevents confusion between *what mathematics or the execution platform dictates* and *what we chose to build* (**ISO 13485:2016 Clause 7.3.3(b)**, **IEC 62304 Clause 5.2.2(i)**, **ISO 14971 Clause 5.2**).

---

## 2. Dependency & Identification Rules (Leaf Node)

- **Item Identifier Prefix:** `FACT-<SLUG>` (governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts)).
- **Permitted Outbound QMS References:** **None.** A `fact` document is a leaf node in the [QMS DAG](qms-spec.md#2-master-traceability-directed-acyclic-graph-dag) and **MUST NOT** reference any other QMS document.
- **Permitted External References:** Normative external primary sources only (IETF RFCs, NIST FIPS, ISO/IEC standards, peer-reviewed papers, or official OS kernel documentation), pinning the exact standard identifier and publication revision/year.

---

## 3. Document-Specific Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

1. **Decision-Constraining External Truths Only:**
   - *Include:* Specific external mathematical, protocol, hardware, or OS platform invariants that directly force a risk control or architectural choice in the repository.
   - *Exclude Textbook Trivia & Spec Dumps:* Do not copy-paste external specification sections or record generic computer-science facts that do not drive a non-obvious design trade-off.
   - *Exclude Engineered Choices & Requirements:* A `fact` document **MUST NOT** contain normative verbs (`SHALL`, `MUST`) directed at the system under design, internal architectural choices, or tunable application constants.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <Domain> Facts`
`Spec: [fact-spec](<relative-path>/spec/fact-spec.md)`

### `## 1. External Normative Sources`
A concise list of the authoritative external specifications or platform docs cited in the file, pinning the identifier, title, and revision/date.

### `## 2. Axioms and Platform Invariants`
Atomic declarative statements of objective external truths prefixed with `FACT-<SLUG>` so downstream documents can cite the axiom in one link instead of re-explaining the external physics, math, or platform behavior.
