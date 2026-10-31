# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Specifications & Architectural Strategy

Architectural governance and documentation across this repository follow a formal specification hierarchy inspired by Medical Device Quality Management Systems (IEC 62304, ISO 13485 Clause 7.3, ISO 14971, and FDA 21 CFR 820.30):

- **System Charter & Requirements**:
  - [`IntendedUse.md`](IntendedUse.md) — System purpose (`IU-*`), target user profiles (`USR-*`), operating environment (`ENV-*`), and contraindications (`CONTRA-*`) (implements [`spec/intended-use-spec.md`](spec/intended-use-spec.md))
  - [`UserNeeds.md`](UserNeeds.md) — Essential stakeholder functional and operational trust needs (`UN-*`) (implements [`spec/user-needs-spec.md`](spec/user-needs-spec.md))
- **Meta-Specifications (`/spec`)**: Standardized document specifications, strict dependency DAGs, semantic item IDs, non-authoritative cross-cutting tags, and change-control rules in [`spec/`](spec/):
  - [`spec/qms-spec.md`](spec/qms-spec.md) — Root QMS governance, master traceability DAG, semantic ID & human-approval change control policy, and tagging rules
  - [`spec/fact-spec.md`](spec/fact-spec.md) — Immutable external axioms and standards (`FACT-*`)
  - [`spec/intended-use-spec.md`](spec/intended-use-spec.md) — System purpose and boundaries (`IU-*`, `USR-*`, `ENV-*`, `CONTRA-*`)
  - [`spec/user-needs-spec.md`](spec/user-needs-spec.md) — Stakeholder capabilities and validation baseline (`UN-*`)
  - [`spec/risk-register-spec.md`](spec/risk-register-spec.md) — Hazard analysis (`HAZ-*`) and Risk Control Measures (`RCM-*`)
  - [`spec/system-requirements-spec.md`](spec/system-requirements-spec.md) — Black-box verifiable system design inputs (`SYS-*`)
  - [`spec/deployable-requirements-spec.md`](spec/deployable-requirements-spec.md) — Software Item allocation and port contracts (`DEP-*`)
  - [`spec/design-spec.md`](spec/design-spec.md) — Software Design Output specifications (`INV-*`, `DEC-*`)
- **Project Design Documents (`Design.md`)**: Each deployable project in the solution contains a dedicated `Design.md` at its root implementing `design-spec.md`, documenting layer boundaries, architectural invariants (`INV-*`), and key technical design choices (`DEC-*`).

## Key Projects

*   **`Percolator.SourceGenerators`**: Compile-time Roslyn source generator for zero-allocation `[ByteArray]` and `[GuidId]` types ([Design Document](Percolator.SourceGenerators/Design.md)).
*   **`Percolator.Domain.Kernel`**: Foundational tactical DDD base abstractions and universal identity tokens ([Design Document](Percolator.Domain.Kernel/Design.md)).
*   **`Percolator.Domain`**: Core domain logic, Signal-grade cryptographic state machines, and channel governance ([Design Document](Percolator.Domain/Design.md)).
*   **`Percolator.Apps.FileTransfer.Domain`**: File transfer bundle geometry, Merkle tree verification, and bitfield algebra ([Design Document](Percolator.Apps.FileTransfer.Domain/Design.md)).
*   **`Percolator.PluginSdk`**: Microkernel extension contracts and out-of-band media descriptors ([Design Document](Percolator.PluginSdk/Design.md)).
*   **`Percolator.Application`**: Use-case orchestration, ingress/egress ratchet pipelines, and outbox delivery ([Design Document](Percolator.Application/Design.md)).
*   **`Percolator.Apps.Chat`**: E2EE messaging, stepped bucket padding, reactions, and disappearing messages ([Design Document](Percolator.Apps.Chat/Design.md)).
*   **`Percolator.Apps.Discovery`**: Privacy-preserving two-phase discovery and blinded rendezvous ([Design Document](Percolator.Apps.Discovery/Design.md)).
*   **`Percolator.Apps.FileTransfer`**: High-throughput multi-stream bulk data transfer orchestration ([Design Document](Percolator.Apps.FileTransfer/Design.md)).
*   **`Percolator.Infrastructure`**: Cross-platform persistence (SQLCipher), gRPC transport, and wire protocols ([Design Document](Percolator.Infrastructure/Design.md)).
*   **`Percolator.Infrastructure.Windows`**: Windows-specific SChannel CNG certificate and NTFS sparse storage adapters ([Design Document](Percolator.Infrastructure.Windows/Design.md)).
*   **`Percolator.Desktop`**: Cross-platform Avalonia UI client with total network stealth startup ([Design Document](Percolator.Desktop/Design.md)).
*   **`Percolator.Simulator`**: Multi-peer localhost network topology simulation environment ([Design Document](Percolator.Simulator/Design.md)).

## Overview

Percolator implements a full end-to-end secure messaging and file-sharing system inspired by the Signal Protocol. This includes the X3DH key agreement protocol, the Double Ratchet algorithm for pairwise sessions, and secure group messaging protocols.

## Architecture Overview

The solution follows an Onion Architecture:

- **Domain Layer**: Core domain libraries (`Percolator.Domain.Kernel`, `Percolator.Domain`, `Percolator.Apps.FileTransfer.Domain`) containing business logic, value types, and cryptographic protocol state machines.
- **Application Layer**: `Percolator.Application`, `Percolator.PluginSdk`, and application plugins (`Percolator.Apps.Chat`, `Percolator.Apps.Discovery`, `Percolator.Apps.FileTransfer`) orchestrating domain models and use cases.
- **Infrastructure Layer**: `Percolator.Infrastructure` and `Percolator.Infrastructure.Windows` providing persistence (EF Core/SQLCipher), gRPC services, and network transport implementations.
- **Presentation Layer**: `Percolator.Desktop` providing the Avalonia UI client.

## Guidance for AI Assistants

*   **Project Goal**: The main objective of this solution is to provide a robust, secure, and well-tested implementation of modern cryptographic protocols.
*   **Key Components**: The core logic is in `Percolator.Domain`. All changes to this library must be accompanied by corresponding tests.
*   **Development Philosophy**: Follow a test-driven development (TDD) approach. Ensure all cryptographic operations use standard, modern, and secure primitives from `.NET`'s `System.Security.Cryptography` namespace. Avoid implementing cryptographic primitives from scratch.
*   **Dependencies**: The project targets a modern .NET version. Ensure cross-platform compatibility.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD).

## Security

The Percolator ecosystem is designed with a security-first approach:

-   **End-to-End Encryption**: Multi-layered encryption protecting all peer-to-peer sessions using the Double Ratchet protocol, providing forward secrecy and post-compromise security.
-   **Cryptographic Identities**: Peer identities are based on a set of public cryptographic keys (following the X3DH protocol), not on certificates.
-   **Denial-of-Service (DoS) Protection**: Service-side rate-limiting and bounded queues to protect against resource exhaustion attacks.
-   **Memory Hygiene**: Zeroization of sensitive key spans and decrypted plaintexts immediately after use.
