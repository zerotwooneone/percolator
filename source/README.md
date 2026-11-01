# Percolator

Percolator is a modern, high-assurance peer-to-peer secure messaging and file-sharing ecosystem implemented in .NET. It incorporates Signal-grade end-to-end cryptography, privacy-preserving blinded rendezvous discovery, and multi-stream high-throughput bulk data transfer.

---

## Architecture Overview

Percolator is structured around strict **Onion Architecture** principles:

```
┌────────────────────────────────────────────────────────┐
│                   Presentation Layer                   │
│                  (Percolator.Desktop)                  │
├────────────────────────────────────────────────────────┤
│                   Application Layer                    │
│    (Percolator.Application, Apps.*, PluginSdk)         │
├────────────────────────────────────────────────────────┤
│                      Domain Layer                      │
│   (Percolator.Domain.Kernel, Percolator.Domain,        │
│          Percolator.Apps.FileTransfer.Domain)          │
├────────────────────────────────────────────────────────┤
│                  Infrastructure Layer                  │
│       (Percolator.Infrastructure, *.Windows)           │
└────────────────────────────────────────────────────────┘
```

*   **Domain Layer**: Core domain logic, immutable cryptographic state machines (Double Ratchet, X3DH key agreement, group sessions), Merkle tree verification, bitfield algebra, and strongly-typed domain primitives. Zero outward dependencies.
*   **Application Layer**: Use-case orchestration, ingress/egress ratchet pipelines, outbox delivery, and modular application plugins (Chat, Discovery, File Transfer).
*   **Infrastructure Layer**: Cross-platform persistence (EF Core with encrypted SQLCipher), gRPC wire transport, Windows SChannel/CNG cryptographic adapters, and NTFS sparse file handling.
*   **Presentation Layer**: Cross-platform desktop interface built on Avalonia UI with total network stealth startup.

### Architectural Governance & Quality Management System (QMS)

Architectural design, risk analysis, and software item requirements follow a formal specification hierarchy inspired by Medical Device Software Life Cycle standards (**IEC 62304**, **ISO 13485 Clause 7.3**, **ISO 14971**, and **FDA 21 CFR 820.30**).

For the complete specification schema, 3-tier regulatory mapping table, and Master Traceability Directed Acyclic Graph (DAG), see:

👉 **[`spec/README.md`](spec/README.md)**

---

## Key Projects

| Project | Role | Documentation |
| :--- | :--- | :--- |
| **`Percolator.Domain.Kernel`** | Base DDD abstractions, value objects, and universal identity tokens | [Design](Percolator.Domain.Kernel/Design.md) |
| **`Percolator.Domain`** | Core cryptographic protocols, ratchet state machines, and channel governance | [Design](Percolator.Domain/Design.md) \| [Requirements](Percolator.Domain/Requirements.md) |
| **`Percolator.SourceGenerators`** | Compile-time Roslyn generators for zero-allocation `[ByteArray]` and `[GuidId]` types | [Design](Percolator.SourceGenerators/Design.md) |
| **`Percolator.Apps.FileTransfer.Domain`** | File transfer bundle geometry, Merkle trees, and bitfield algebra | [Design](Percolator.Apps.FileTransfer.Domain/Design.md) |
| **`Percolator.PluginSdk`** | Microkernel extension contracts and out-of-band media descriptors | [Design](Percolator.PluginSdk/Design.md) |
| **`Percolator.Application`** | Ingress/egress ratchet pipelines, use-case orchestration, and outbox delivery | [Design](Percolator.Application/Design.md) \| [Requirements](Percolator.Application/Requirements.md) |
| **`Percolator.Apps.Chat`** | E2EE messaging, stepped bucket padding, reactions, and disappearing messages | [Design](Percolator.Apps.Chat/Design.md) \| [Requirements](Percolator.Apps.Chat/Requirements.md) |
| **`Percolator.Apps.Discovery`** | Privacy-preserving two-phase discovery and blinded rendezvous | [Design](Percolator.Apps.Discovery/Design.md) \| [Requirements](Percolator.Apps.Discovery/Requirements.md) |
| **`Percolator.Apps.FileTransfer`** | High-throughput multi-stream bulk data transfer orchestration | [Design](Percolator.Apps.FileTransfer/Design.md) \| [Requirements](Percolator.Apps.FileTransfer/Requirements.md) |
| **`Percolator.Infrastructure`** | Cross-platform persistence (SQLCipher), gRPC transport, and wire protocols | [Design](Percolator.Infrastructure/Design.md) \| [Requirements](Percolator.Infrastructure/Requirements.md) |
| **`Percolator.Infrastructure.Windows`** | Windows CNG certificate storage and NTFS sparse storage adapters | [Design](Percolator.Infrastructure.Windows/Design.md) |
| **`Percolator.Desktop`** | Cross-platform Avalonia UI client with network stealth startup | [Design](Percolator.Desktop/Design.md) |
| **`Percolator.Simulator`** | Multi-peer localhost network topology simulation environment | [Design](Percolator.Simulator/Design.md) |

---

## Security & Assurance

*   **End-to-End Encryption**: Multi-layered session encryption utilizing the Double Ratchet algorithm and X3DH key agreement, providing forward secrecy and post-compromise security.
*   **Cryptographic Peer Identities**: Peer authentication relies strictly on public cryptographic key bundles rather than centralized certificates or authority chains.
*   **Denial-of-Service Defense**: Service-side rate-limiting, bounded work queues, and two-phase discovery to safeguard resources against prekey pool exhaustion attacks.
*   **Memory Hygiene**: Explicit zeroization of sensitive cryptographic key material and plaintext buffers immediately following consumption.

---

## Building and Testing

### Prerequisites
*   [.NET 9 SDK](https://dotnet.microsoft.com/download) or later
*   Platform C++ build tools (for native SOUP library bindings where applicable)

### Build
```bash
dotnet build Percolator.sln
```

### Test
```bash
dotnet test Percolator.sln
```

---

## Developer & AI Guidelines

*   **Principle of Verification (Verify Before Acting)**: Never assume class names, interfaces, or method signatures from training memory. Always inspect the codebase using search and file viewing tools before making modifications.
*   **Test-Driven Development (TDD)**: Follow the Red-Green-Refactor cycle. All new domain logic and bug fixes must be verified with deterministic unit or integration tests.
*   **Strongly-Typed Identifiers**: Raw `Guid` primitives must not be exposed in public domain APIs. Use the `[GuidId]` source generator to declare intention-revealing value objects.
*   **No Raw Byte Arrays**: Avoid raw `byte[]` in public domain models; encapsulate binary data in domain-specific `[ByteArray]` value types to eliminate ambiguity.
*   **Standard Cryptography Only**: Always rely on standard, audited cryptographic primitives in `System.Security.Cryptography` or vetted native libraries (libsignal). Never implement bespoke primitives.
