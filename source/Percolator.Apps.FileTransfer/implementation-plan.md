# Percolator.Apps.FileTransfer: Implementation Plan Index

## Refinement Status Gate
- [x] Lens 1: Scope & Feature Slice Framing (Status: Approved)
- [x] Lens 2: Onion Architecture & Primitive Obsession (Status: Approved)
- [ ] Lens 3: Threat Modeling & Security Posture (Status: In Progress)
- [ ] Lens 4: High-Value, Low-Brittle Unit Test Design (Status: Pending)
- [ ] Lens 5: Cross-Layer Infrastructure Alignment (Status: Pending)
- [ ] Implementation Gate: LOCKED (Requires Approval of Lenses 1-5)

---

## Architectural Plan Separation (Onion Architecture Alignment)

To enforce clean architectural boundaries and facilitate efficient, token-conscious refinement and staged implementation, the implementation plan is divided into two dedicated documents:

1. **[Domain Implementation Plan (`domain-plan.md`)](./domain-plan.md)**:
   - **Target**: Pure Domain Model (`Percolator.Apps.FileTransfer.Domain`).
   - **Scope**: Domain Aggregates (`TransferSession`, `FileTransferFilterPolicy`), Entities (`SwarmMember`, `TransferManifest`, `FileEntry`), Strongly Typed Value Objects (`Percolator.SourceGenerators`), Formal Domain Events (`IDomainEvent`), and Invariant Math (Merkle verification, boundary spanning).
   - **Commitment**: Implemented and tested **first** with zero dependencies on transport, storage I/O, or `PluginSdk`.

2. **[Application Implementation Plan (`application-plan.md`)](./application-plan.md)**:
   - **Target**: Application Orchestration & Ports (`Percolator.Apps.FileTransfer`).
   - **Scope**: Plugin Lifecycle (`FileTransferPlugin`), Ingress Edge Pre-Filtering (`FileTransferIngressFilter`), Wire Payloads & Out-of-Band Framing (`IStreamFramePayload`), Outbound Ports (`ITransferStreamPool`, `IDistributedTrackerPort`, `IDesktopFileStorage`, `ITransferRateLimiter`, `ITransferSessionRepository`), and Minimal CQRS (`IFileTransferActionHandler`, `IFileTransferQueryService`).
   - **Commitment**: Implemented and integrated upon the verified domain model.
