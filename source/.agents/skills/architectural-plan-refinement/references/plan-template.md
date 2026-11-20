# Implementation Plan Template

Use this template when authoring or restructuring implementation plans to maintain consistency and enforce multi-turn refinement gates.

---

```markdown
# [Feature / Subsystem Name] Implementation Plan

## Refinement Status Gate
- [ ] Lens 1: Scope & Feature Slice Framing (Status: Pending)
- [ ] Lens 2: Onion Architecture & Primitive Obsession (Status: Pending)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: Pending)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## 1. Scope & Architectural Boundaries
### 1.1 In-Scope Capabilities
- [Capability 1]
- [Capability 2]

### 1.2 Explicit Non-Goals
- [Out of scope item 1]
- [Out of scope item 2]

---

## 2. Vertical Feature Slice Architecture
Grouped by feature capability and behavior:

### Feature Slice: `Features.[FeatureOne]`
- **Value Objects**: [Strongly typed domain concepts with 0 cast operators]
- **Contracts / Workflows**: [Interfaces and orchestrators]
- **Payloads**: [Immutable wire / messaging DTOs]

### Feature Slice: `Features.[FeatureTwo]`
- **Value Objects**: [...]
- **Contracts / Workflows**: [...]

### Common Subsystem Ports (`Ports/`)
- `I[Feature]ClientPort`: [...]
- `I[ExternalDependency]Port`: [...]

### Ingress & Envelope Demuxing (`Ingress/`)
- `[Feature]PayloadHandler`: [Enforces anti-impersonation and sender authentication]

---

## 3. Threat Model & Privacy Invariants
- **Protocol Parity**: [Signal zero-presence / zero-metadata alignment]
- **Resource Starvation Defenses**: [OPK / bandwidth / memory protections]
- **Admission Gates**: [PreKey & handshake requirements]
- **Route & Endpoint Privacy**: [IP masking / relayed route enforcement]

---

## 4. High-Value Unit Testing Matrix
| Test Category | Target Feature / State | Verifies (Observable Behavior) | Low-Brittle Guarantee |
| :--- | :--- | :--- | :--- |
| Admission Gate | Missing Signed PreKey | Returns rejection result | Tests result contract, no private state |
| Starvation Defense | Candidate Discovery Query | OPK bundle is never fetched | Asserts prekey port was uninvoked |
| Epoch Ratchet | 24-hr UTC Window Transition | Generates distinct locators | Compares output tokens across epochs |
| Fault Tolerance | Batch Candidate Dialing | Partial failure continues batch | Verifies output list contains successes |

---

## 5. Cross-Layer Infrastructure Alignment
- **Protobuf Contracts**: `[subsystem].proto` schemas and message types.
- **SQLite Persistence**: Target tables, primary keys, and indices.
- **Adapters**: Concrete realizations of ports in `Percolator.Infrastructure`.
- **Background Pruning**: Scheduled workers for transient record expiration.
```
