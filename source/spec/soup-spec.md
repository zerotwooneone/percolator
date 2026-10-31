# Software of Unknown Provenance Specification (`soup-spec`)

Spec: [qms-spec](qms-spec.md)

## 1. Purpose and Regulatory Basis

A **SOUP Specification** (`soup`) defines the configuration identification, architectural quarantine, environmental prerequisites, functional boundaries, known anomaly assessments, and lifecycle maintenance rules for any third-party, commercial off-the-shelf (COTS), or open-source software component incorporated into the product.

In medical device and safety-critical software, third-party code cannot be assumed to have been developed under formal design controls. Consequently, international standards and regulatory guidance mandate rigorous evaluation of third-party software to ensure it does not compromise system safety, security, or performance:

- **IEC 62304:2006+AMD1:2015 Clause 3.24**: Defines SOUP as software already developed and generally available that was not developed specifically for incorporation into the medical device, or software previously developed for which adequate development process records are unavailable.
- **IEC 62304:2006+AMD1:2015 Clause 4.3 & 5.1.4 (Software Safety Classification)**: Establishes that documentation and verification rigor scales with the component's assigned safety class (**Class A, B, or C**), or inherits the classification of the system unless architecturally segregated. Explicit justification of the classification rationale is mandatory.
- **IEC 62304:2006+AMD1:2015 Clause 5.3.3 & 5.3.4**: Mandates specifying the functional, performance, system hardware, and software prerequisites (operating environment, runtimes, memory, architecture) necessary for proper operation of each SOUP item.
- **IEC 62304:2006+AMD1:2015 Clause 5.3.5 & 7.1.4 (Architectural Segregation)**: Requires explicit architectural isolation mechanisms so that a failure in a SOUP item cannot compromise adjacent safety-critical functions.
- **IEC 62304:2006+AMD1:2015 Clause 7.1.2 & 7.1.3**: Mandates identifying potential failure modes and sequences of events leading to hazards caused by SOUP, including formal evaluation of the supplier's published list of **known anomalies** (bugs, errata, vulnerabilities).
- **IEC 62304:2006+AMD1:2015 Clause 8.1.2**: Requires precise configuration identification (title, supplier, complete version number, build/commit hash, package/binary digests).
- **IEC 62304:2006+AMD1:2015 Clause 6.1 & 9.5**: Requires monitoring post-market vendor updates, evaluating known residual anomalies at release, and maintaining a patch/obsolescence plan.
- **FDA Guidance: *Off-The-Shelf Software Use in Medical Devices* (August 2023)**: Replaces legacy "Level of Concern" with Basic vs. Enhanced Documentation, mandating product description, environmental prerequisites, hazard mitigation, boundary verification, and continuous maintenance. Section V.A.2 explicitly mandates documenting unused/restricted functions and how inadvertent execution is prevented.
- **ISO 14971:2019 Clause 5.4 & 7.1**: Requires hazard analysis of third-party dependency failures and implementation of risk controls (e.g., defensive wrappers, input sanitization, fail-safe fallbacks) residing *outside* the unverified component.

---

## 2. Dependency & Identification Rules

- **Item Identifier Prefix:** `SOUP-<SLUG>`, tagged with a boundary verification method (`[Verify: Integration | Simulation | StaticAnalysis]`) per [`qms-spec`](qms-spec.md#33-lightweight-verification--risk-control-tags-verify--control-) and governed by [Change Control](qms-spec.md#32-design-change-control-never-move-the-goalposts). Evaluated anomaly items utilize `SOUP-<SLUG>-ANO-<NAME>` (or cite official external defect identifiers such as `CVE-YYYY-XXXX`).
- **Permitted Outbound QMS References:**
  - `fact` (`FACT-*`): Links to documented platform runtime invariants, hardware properties, or external protocol specifications.
  - `risk-register` (`HAZ-*`): Links to hazards that can be triggered or exacerbated by SOUP anomalies, crashes, or unhandled exceptions.
- **Permitted Inbound QMS References:**
  - `system-requirements` (`SYS-*`) and `deployable-requirements` (`DEP-*`): Cite `SOUP-<SLUG>` items when allocating functionality to or wrapping a third-party dependency.
  - `design` (`DEC-*`, `INV-*`): Cites `SOUP-<SLUG>` to define architectural segregation, adapter boundaries, and quarantine policies.
- **Prohibited Outbound References:** **MUST NOT** cite `user-needs` (`UN-*`) or `intended-use` (`IU-*`) directly (SOUP items are implementation ingredients, not stakeholder desires).

---

## 3. Inclusion & Exclusion Rules

In addition to the universal rules in [`qms-spec`](qms-spec.md):

### 3.1 Proportional Documentation by Safety Classification (IEC 62304 Clause 4.3)
Documentation rigor must match the component's assigned safety class:
- **Class A (No Injury / Pure Utility)**: Trivial utilities, UI styling/icon packs, or development-only tools where failure cannot cause harm or privacy compromise. Requires only Section 1 (Identification & Provenance) and Section 2 (Prerequisites). Exempt from itemized `SOUP-*` functional envelopes and anomaly matrices.
- **Class B (Non-Serious Harm / Operational Disruption)**: Components whose failure causes recoverable system errors, denial of service, or non-critical message delivery delay. Requires all sections (1 through 5).
- **Class C (Critical Safety / Severe Privacy Compromise)**: Cryptographic engines, core persistence databases, credential storage, or network transport primitives where failure, crash, or memory corruption directly leads to secret key compromise, identity leak, or irrecoverable data loss. Requires comprehensive Section 1–5 documentation with mandatory architectural quarantine and explicit classification rationale.

### 3.2 Architectural Quarantine & Hexagonal Segregation (IEC 62304 Clause 5.3.5)
In strict Hexagonal / Onion DDD architectures:
1. **Adapter Containment**: Every Class B or C SOUP item **MUST** be quarantined within an `Infrastructure` adapter behind an abstract domain port/interface.
2. **Zero Domain Contamination**: Third-party types, raw handles (`IntPtr`, `SafeHandle`), vendor DTOs, or native exceptions **MUST NEVER** cross the boundary into `Domain` entities. Adapters must translate third-party data structures into domain Value Objects and convert vendor errors into `DomainResult` failures.
3. **Fault Containment**: If a native or unmanaged SOUP component experiences an unhandled exception or memory error, the enclosing adapter must capture and isolate the failure without crashing the host process or leaving unzeroized secret memory on the managed heap.

### 3.3 Reconciling Baseline Records with "Code-as-Truth" (Rule 5.2)
To comply with both IEC 62304 Clause 8.1.2 (mandatory DHF version record) and [`qms-spec` Rule 5.2](qms-spec.md#103) (Code-as-Truth):
- The SOUP specification records the **Evaluated and Approved Baseline** (exact version, release date, and cryptographic commit hash or package SHA-256 digest evaluated against hazards).
- Automated CI tooling (package lockfiles, Dependabot, SBOM scanners) verifies that the repository's active build manifest matches this approved baseline.
- Never duplicate code interface declarations or class method signatures in markdown; specify only the functional and contractual obligations (`SOUP-<SLUG>`).

### 3.4 Focused Anomaly Evaluation (No "CVE Dumps")
- **In Scope for Section 4**: Only evaluate known bugs, issue tracker items, and published CVEs that are **reachable** within the application's constrained functional usage envelope, or known residual defects in the approved version that require compensating risk controls (`HAZ-*`).
- **Out of Scope (Excluded)**: Do not copy-paste hundreds of historical, resolved, or unreachable CVEs from unused subsystems. Continuous background vulnerability monitoring is performed via automated tooling (e.g., `dotnet list package --vulnerable`), not static document clutter.

### 3.5 Document Granularity (Dedicated Spec vs. Multi-Item Catalog)
To prevent document sprawl:
- **Dedicated SOUP Document (`soup/<component-name>.md`)**: Reserved for foundational, high-risk, or Class C components (e.g., `Signal.Interop`, `SQLCipher`, TLS transport engines).
- **SOUP Catalog Document (`soup/platform-libraries.md`)**: Groups lower-risk Class A and B packages (e.g., serialization utilities, dependency injection helpers) into a single concise catalog without redundant boilerplate.

### 3.6 Strict Exclusions (What Does NOT Belong in a SOUP Document)
1. **Proprietary First-Party Code**: Custom code written in-house is governed by `deployable-requirements-spec` and `design-spec`, never SOUP.
2. **Forked / Modified Vendor Source**: If a third-party library is modified in-house, it is no longer off-the-shelf and must either be contributed upstream or reclassified and verified as custom software under full design controls.
3. **Application Business Logic & User Needs**: Clinical workflows and user needs belong in `user-needs-spec` or `system-requirements-spec`.
4. **Internal White-Box Unit Testing of Vendor Code**: Do not specify unit test suites or code coverage for the vendor's internal source. Verification of SOUP is performed strictly via black-box integration, functional boundary testing, and adapter contract checks.

---

## 4. Allowed Headings and Section Definitions

Implementing documents **MUST** use only the headings below and **MUST NOT** repeat these instructional explanations:

### `# <Component/Subsystem> SOUP Specification`
`Spec: [soup-spec](<relative-path>/spec/soup-spec.md)`

### `## 1. Component Identification and Provenance`
A structured table defining the provenance, safety class, and immutable identity of the third-party software item:
- **Component Name:** Title of the library, runtime, or subsystem.
- **Supplier / Maintainer:** Organization, company, or open-source community maintaining the component.
- **Safety Classification & Rationale:** `Class A` (Exempt from §3–§5), `Class B`, or `Class C` (IEC 62304 Clause 4.3) with explicit hazard rationale justifying why failure does or does not cause critical harm.
- **Approved Version & Hash:** Pinned semantic version number and cryptographic hash (git commit SHA or package/binary SHA-256).
- **License Model:** Software license terms (e.g., MIT, Apache-2.0, BSD-3-Clause, Commercial).
- **Delivery & Linking Format:** Source, pre-compiled static/dynamic library (`.dll`, `.so`), NuGet package, or OS system library.
- **Upstream Repository URL:** Authoritative primary source location.

### `## 2. Hardware and Software Prerequisites`
System environment and platform requirements necessary to support proper operation of the SOUP item (IEC 62304 Clause 5.3.4):
- **Supported Operating Systems & Runtimes:** OS editions, BCL runtime version, C-runtime dependencies.
- **Hardware Architecture:** Target architectures (x64, ARM64), memory limits, storage requirements.
- **Native / Unmanaged Prerequisites:** Secondary shared libraries, system drivers, or cryptographic providers required.

### `## 3. Specified Functional and Performance Capabilities`
*(Required for Class B and C; exempt for Class A)*

#### `### 3.1 Active Functional & Capacity Envelope`
Atomic, numbered specifications (`SOUP-<SLUG>`) defining the exact functional subset, API capabilities, throughput, latency, concurrency model (thread-safety constraints), and capacity constraints utilized by the device (IEC 62304 Clause 5.3.3).

Each item must include:
- Its boundary verification method tag (`[Verify: Integration | Simulation | StaticAnalysis]`).
- The enclosing **Architectural Quarantine Adapter** or outbound Domain Port that isolates this capability from the domain core (IEC 62304 Clause 5.3.5).

#### `### 3.2 Unused and Prohibited Capabilities (FDA OTS Guidance §V.A.2)`
Itemized list of functions, classes, or subsystem capabilities that are present within the SOUP package/binary but are **not used** by the application, accompanied by the architectural quarantine mechanism ensuring they cannot be inadvertently invoked from application code.

### `## 4. Known Anomalies and Cybersecurity Evaluation`
*(Required for Class B and C; exempt for Class A)*

Formal assessment of supplier-published bugs, issue tracker records, and NVD/CVE entries (IEC 62304 Clause 7.1.3 & 9.5, FDA OTS Guidance). Each evaluated defect is identified either by official supplier identifier (`CVE-YYYY-XXXX`, `GH-#<ID>`) or repository anomaly identifier (`SOUP-<SLUG>-ANO-<NAME>`):
- **External Reference / Defect:** External reference number or defect ID.
- **Supplier Summary:** Description of the defect or vulnerability.
- **Relevance & Reachability:** Assessment of whether the defect is reachable within the application's constrained functional usage envelope.
- **Safety & Security Impact:** Evaluation of whether the anomaly can trigger a hazard (`HAZ-*`).
- **Compensating Risk Control:** Architectural quarantine, defensive input validation, memory zeroization, or software workaround neutralizing the defect.

### `## 5. Maintenance and Obsolescence Plan`
*(Required for Class B and C; exempt for Class A)*

Procedures for ongoing monitoring, update qualification, and end-of-life management (IEC 62304 Clause 6.1):
- **Vulnerability Monitoring Cadence:** Scheduled review frequency against NVD, GitHub Dependabot, or vendor errata lists.
- **Patch Qualification & Regression Verification:** Required test suite execution prior to accepting vendor minor/patch updates.
- **Obsolescence & Deprecation Contingency:** Alternative migration path if the upstream maintainer abandons the component.
