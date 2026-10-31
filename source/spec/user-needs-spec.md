# User Needs Specification (`user-needs-spec`)

## 1. Purpose and QMS Basis

A **User Needs Specification** (`user-needs`) document captures essential stakeholder capabilities and usability/operational expectations in the problem domain ("voice of the user"). In the QMS V-Model, User Needs translate the `intended-use` charter into observable user outcomes that drive `system-requirements` (Design Inputs) and serve as the mandatory acceptance baseline for Design Validation.

- **FDA 21 CFR 820.30(c) & 820.30(g)** (*Design input & Design validation*): Ensuring design inputs address user needs and validating that the finished system conforms to those needs under actual or simulated use.
- **ISO 13485:2016 Clause 7.3.2 & 7.3.7**: Capturing stakeholder functional and usability expectations prior to engineering design.
- **IEC 62304:2006+AMD1:2015 Annex B.5.2**: Separating problem-domain stakeholder needs from verifiable system requirements and risk controls.

---

## 2. Dependency Rules

- **Permitted Outbound QMS References:** `intended-use` and `fact` documents only (linked inline where relevant).
- **Prohibited Outbound References:** **MUST NOT** reference `risk-register`, `system-requirements`, `deployable-requirements`, or `design` documents.

---

## 3. Inclusion, Exclusion, & Anti-Duplication Rules

1. **Single Header Spec Link:** Reference this spec once immediately below the H1 title:
   `Spec: [user-needs-spec](<relative-path>/spec/user-needs-spec.md)`
2. **Solution-Free "Voice of the User" (What, Never How):** Every need must state *what* the user needs to accomplish or experience in the problem domain, **never** the architectural or engineering mechanism used to achieve it.
   - *Include:* *"Send and receive messages even when communicating peers are offline or cannot connect directly."*
   - *Exclude:* *"...by routing or buffering messages through untrusted store-and-forward relay nodes."* (The relay mechanism is a System Requirement / Design choice derived from combining this User Need with the serverless constraint in `intended-use`).
3. **Simulated-Use Validatability (Design Validation vs. Design Verification):** Under *FDA 21 CFR 820.30(g)*, every User Need must be directly testable by observing a representative user operating the finished product in simulated or actual use. **Exclude** properties that require protocol inspection, cryptographic proofs, or adversarial wire analysis to verify:
   - *Exclude Cryptographic & Wire Invariants:* Forward secrecy, post-compromise recovery, group epoch key rotation, metadata blinding, and wire-level indistinguishability (e.g., whether a network observer can tell if a peer is blocked vs. offline). These belong in `risk-register` and `system-requirements`.
   - *Exclude Protective Warnings / Risk Controls:* Specific threat alerts (such as MITM key-change warnings) are *Information for Safety / Protective Risk Control Measures* (`RCM-*`) in `risk-register`, not primary User Needs.
4. **Essential Validation Baseline Only (No "Nice-to-Haves"):** Because failing any single User Need means the product fails Design Validation, every item in `user-needs` must be **essential**—if the capability were absent, the system would fail its `intended-use`. Optional conveniences, secondary workflows, or "nice-to-have" features (such as automated peer discovery, UI conveniences, or optional plugins) **MUST** be excluded.
5. **Atomic Needs (No Compound Bundling):** Each item must describe one cohesive user capability (e.g., separate *compartmentalized personas on a device* from *operating a single identity across multiple devices*) rather than bundling multiple distinct validation scenarios into one paragraph.
6. **Do Not Repeat `intended-use` Definitions:** Never restate the system's foundational guarantees (e.g., *"without registering with a central authority or binding to phone numbers/emails"* or *"without exposing plaintext to third parties"*), user profiles, or adversary environment already defined in `intended-use`. Link inline to `intended-use` instead.
7. **History Policy:** In-file revision history tables are prohibited; `git log` is authoritative.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these section explanations:

### `# <System Name> User Needs`
`Spec: [user-needs-spec](<relative-path>/spec/user-needs-spec.md)`

### `## 1. Core Functional Needs`
Atomic, solution-free statements of the essential communication and identity capabilities users must be able to perform in simulated-use validation for the system to fulfill its intended use.

### `## 2. Sovereignty, Privacy, and Operational Trust Needs`
Atomic, user-observable expectations regarding contact trust verification, peer-level privacy controls, session activation/locking, data retention lifecycle, and zero-touch background network participation within user-defined resource limits.
