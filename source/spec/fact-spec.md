# Fact Specification (`fact-spec`)

## 1. Purpose and QMS Basis

A **Fact Specification** (`fact`) document records immutable, externally verifiable truths—mathematical theorems, cryptographic primitive properties, published RFC/ISO protocol invariants, or operating system kernel behaviors—that constrain the system's design and risk analysis.

In a Medical Device Quality Management System (QMS), separating external axioms from engineered requirements prevents confusion between *what mathematics or the execution platform dictates* and *what we chose to build*.

- **ISO 13485:2016 Clause 7.3.3(b)** & **FDA 21 CFR 820.30(c)**: Identification of external standards and baseline physical/technical constraints.
- **IEC 62304:2006+AMD1:2015 Clause 5.2.2(i)**: Separation of external standards and platform characteristics from software requirements.
- **ISO 14971:2019 Clause 5.2**: Objective technical and environmental facts grounding hazard identification.

---

## 2. Dependency Rules (Leaf Node)

A `fact` document is a **Leaf Node** in the QMS Directed Acyclic Graph (DAG):

- **Permitted Outbound QMS References:** **None.** A `fact` document **MUST NOT** reference `intended-use`, `user-needs`, `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.
- **Permitted External References:** Normative external primary sources only (e.g., IETF RFCs, NIST FIPS, ISO/IEC standards, peer-reviewed papers, or official OS kernel documentation).

---

## 3. Anti-Duplication & Stability Rules

1. **Single Header Spec Link:** The document must reference this spec once immediately below the H1 title:
   `Spec: [fact-spec](<relative-path>/spec/fact-spec.md)`
2. **State Only What Constrains Upstream Decisions:** Do not dump entire external RFCs or API manuals into a `fact` file. Record only the specific external invariants (e.g., Windows SChannel's out-of-process CNG key container requirement, or AEAD ciphertext length preservation) that directly force an architectural decision or risk control in the repository.
3. **No Engineered Requirements or Tunable Constants:** A `fact` document **MUST NOT** contain normative verbs (`SHALL`, `MUST`) directed at our software, nor internal application constants (such as buffer sizes or timeouts).
4. **History Policy (Git vs. In-File):** Prose revision tables are prohibited (`git log` is authoritative). **Exception:** Because external standards evolve outside our git repository, each cited external source must pin its exact standard number, edition/revision, and publication year.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <Domain> Facts`
`Spec: [fact-spec](<relative-path>/spec/fact-spec.md)`

### `## 1. External Normative Sources`
A concise list of the authoritative external specifications or platform docs cited in the file, pinning the identifier, title, and revision/date.

### `## 2. Axioms and Platform Invariants`
Concise declarative statements of objective external truths, grouped by topic or tagged with a short stable anchor (`FACT-<SLUG>`) so downstream `risk-register`, `requirements`, or `design` documents can reference the axiom in one link instead of re-explaining the external physics, math, or OS kernel quirk.
