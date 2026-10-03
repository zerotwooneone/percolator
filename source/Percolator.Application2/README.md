# Percolator.Application2

`Percolator.Application2` represents the **Application (Use Case & Orchestration) Layer** of the Onion Architecture. It serves as the central coordination engine of the Percolator node, translating domain business rules and cryptographic primitives into end-to-end communication workflows while shielding the core domain from external infrastructure concerns.

Dependencies in this layer point strictly inward toward `Percolator.Domain` and across to the plugin interfaces defined in `Percolator.PluginSdk`.

---

## 1. Architectural Role: What Belongs in Application2 (In Scope)

The Application layer coordinates the execution of application use cases, governs data ingress and egress pipelines, routes payloads to application plugins, and maintains delivery persistence guarantees.

| Architectural Responsibility | High-Level Scope & Purpose |
| :--- | :--- |
| **Use Case Orchestration** | Directs application workflows by coordinating domain aggregates, entities, and outbound ports without implementing low-level technical infrastructure. |
| **Ingress Pipeline & Verification** | Receives inbound wire envelopes from network transports, enforces packet size constraints, checks sender authorization/reputation, executes cryptographic ratchet steps via domain ports, and verifies associated data authentication. |
| **Plugin Substrate & Dispatch ($O(1)$ Jump Table)** | Routes decrypted application payloads directly to registered application handlers (such as Chat, Discovery, and File Transfer) using constant-time identifier dispatch without inspecting higher-level application payload contents. |
| **Memory & Plaintext Hygiene** | Manages transient decrypted message buffers and enforces zeroization of sensitive plaintext payloads immediately after dispatching to the target application handler. |
| **Egress Framing & Packaging** | Encapsulates application plugin payloads with application identifiers, coordinates cryptographic ratchet advances with associated data header construction, and prepares wire frames for transport. |
| **Always Outbox First (Delivery Guarantee)** | Enforces the architectural invariant that all outbound frames are persisted to an atomic outbox repository before network transmission is attempted, guaranteeing zero message loss across application restarts or crashes. |
| **Egress Stream Flow Control & Backpressure** | Interacts with stream registries to detect open direct and relay channels, pushing frames immediately along active streams while safely falling back to retry queues when streams are closed or backpressured. |
| **Retry Policies & Exponential Backoff** | Implements non-blocking delivery workers and retry calculators with exponential backoff and jitter to govern failed or unacknowledged message redelivery. |
| **Transport Route Resolution** | Coordinates dynamic delivery routing decisions by evaluating open connection states, peer endpoint reachability, and home relay mailbox fallbacks. |
| **Profile Encryption & Contact Consent Coordination** | Coordinates user display metadata encryption (using symmetric profile keys) and manages contact request lifecycles (inbound request deduplication, approval, rejection, and blocking) outside the cryptographic channel domain. |
| **Outbound Port Definitions** | Defines ports (interfaces) for external infrastructure capabilities required by application use cases (such as stream registries, outbox repositories, session storage, reachability probing, and contact persistence). |

---

## 2. What Does NOT Belong in Application2 (Out of Scope)

To maintain strict onion architecture boundaries, the following concerns are explicitly excluded from `Percolator.Application2` and belong in other architectural layers:

| Excluded Concern | Responsible Layer | Architectural Reason |
| :--- | :--- | :--- |
| **Pure Cryptographic State & Ratchet Mathematics** | Domain (`Percolator.Domain`) | Double Ratchet key derivations, Diffie-Hellman calculations, KDF chains, and epoch advancement logic belong exclusively to the domain layer. |
| **Channel Membership & Governance Invariants** | Domain (`Percolator.Domain`) | Business rules governing channel participant rosters, admin roles, and epoch security invariants are pure domain aggregate logic. |
| **Protobuf Serialization & DTO Compilations** | Infrastructure | Compiling `.proto` files, managing Protobuf code-generated DTOs, and serializing messages to Protobuf wire formats are technical serialization mechanisms that belong strictly in infrastructure adapters. |
| **Transport Streaming & Socket Protocols** | Infrastructure | Low-level gRPC channels, HTTP/2 streaming, TCP/UDP sockets, TLS certificate management, and stream reconnection loops are technical transport concerns. |
| **Database & Persistence Drivers** | Infrastructure | SQLite/SQLCipher database drivers, SQL connection lifecycles, EF Core contexts, table definitions, and migrations belong in infrastructure storage adapters. |
| **Application Domain Logic (Chat, Discovery, Files)** | Application Plugins (`Percolator.Apps.*`) | Chat message history, thread hierarchies, discovery advertisements, rendezvous handshakes, and file manifest parsing belong in their respective application plugins, not in the core application host. |
| **Out-of-Band Data Transfer** | Application Plugins / Infrastructure | High-volume file chunk transfers, torrent transfers, and high-bandwidth streaming bypass the Double Ratchet pipeline and are orchestrated out-of-band by dedicated file sharing services. |
| **Platform-Specific Credential Vaults** | Infrastructure | Operating system keyrings (Windows DPAPI, macOS Keychain, Linux Keyutils) and local filesystem access permissions belong in platform infrastructure adapters. |
| **Presentation & UI State** | Presentation / UI | ViewModels, UI data bindings, navigation stacks, notifications, and user input capture belong exclusively to presentation projects (e.g., WPF, Avalonia, or CLI). |

---

## 3. Core Architectural Invariants

### 3.1 Always Outbox First
The application host guarantees at-least-once message delivery under arbitrary network failures and process crashes. Outgoing messages submitted by application plugins are atomically written to the persistent outbox repository **before** any attempt is made to write to an active network stream. 

If an active stream is ready and healthy, transmission occurs immediately as an in-memory optimization. If the stream is closed or backpressured, the job safely remains queued for background worker delivery.

### 3.2 Strict Inward Dependencies
The Application layer has zero dependencies on infrastructure implementations, concrete database libraries, gRPC client libraries, or UI frameworks. All interactions with external technical mechanisms occur through outward-facing ports.

### 3.3 Application Payload Opacity
The application host treats application payloads as opaque byte buffers tagged with a single-byte application identifier. The host is responsible for authenticating and decrypting the transport envelope, but never parses, validates, or mutates the internal structure of the plugin's payload.

### 3.4 Temporal Inversion
All pipeline steps, outbox retries, and contact request workflows obtain current time exclusively through inverted time providers, ensuring that all use cases can be verified deterministically in unit tests without arbitrary clock delays.

### 3.5 Cryptographic Logging Guardrails
The application layer and all its pipelines must strictly honor cryptographic logging guardrails (`CryptographyOptions.EnableCryptographicMaterialLogging = false` by default). Pipeline loggers, diagnostic traces, and exception handlers must **never** record sensitive cryptographic key material (root keys, chain keys, message keys, ephemeral private keys, or intermediate KDF digests) or decrypted application plaintexts in log outputs. Only sanitized operational metadata (e.g. ChannelId, PublicIdentityId, DeviceId, MessageCounter, frame byte lengths) is permissible in log sinks.
