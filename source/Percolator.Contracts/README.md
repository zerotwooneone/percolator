# Percolator.Contracts

This project contains the formal data contracts for the Percolator system, defined exclusively using Protocol Buffers (`.proto` files). It serves as the single source of truth for the structure of data that is serialized and transmitted between different parts of the system.

## Core Principles

- **Contracts Only**: This project MUST NOT contain any C# code, business logic, or helper classes. Its sole responsibility is to define data structures.
- **DDD Alignment**: By strictly defining our data contracts here, we create a clear boundary between the application's data structures and its domain logic.

## Architectural Goals

The system is designed with a two-layer architecture, which is reflected in the contracts:

1.  **Public Transport Layer**: A minimal, secure-by-default public interface. Its only job is to establish secure sessions and move opaque, encrypted data between peers.
2.  **Internal Application Layer**: A rich, feature-driven layer that operates exclusively over the secure channels established by the transport layer.

---

## Rules for Modifying Contracts

To maintain stability and backward compatibility, all changes to the `.proto` files in this project must adhere to the following rules. This is a critical process that requires careful planning.

-   **Prioritize Backward Compatibility**: The public-facing transport layer must remain as stable as possible. Never change the field numbers of existing fields. Renaming a field is a breaking change and should be avoided.
-   **Adding New Fields**: New fields can be added, but they MUST be `optional`. This ensures that older clients can still parse messages from newer clients without errors.
-   **Deprecating Fields**: If a field is no longer needed, it should be marked with the `[deprecated=true]` option rather than being removed immediately. This signals to developers that the field should no longer be used and will be removed in a future major version.
-   **Removing Fields**: Removing a field is a significant **breaking change**. It should only be done after a thorough analysis of its impact across the entire system and after it has been deprecated for a reasonable period. The process we just followed (analyze, propose, approve, implement) is the model for this.
-   **Rebuild After Changes**: After any modification to a `.proto` file, the entire solution MUST be rebuilt. This regenerates the C# code and immediately surfaces any compilation errors in the consuming projects, which must then be fixed.

---

## Peer Identification and `PeerId`

In Percolator's peer-to-peer model, identity is handled with a simple and secure approach that cleanly separates local application identifiers from cryptographic identifiers.

- **`PeerId` is a Local-Only Identifier**: A `PeerId` is a non-cryptographic identifier (e.g., a GUID) used to uniquely reference a peer *within the local application instance only*. It allows the application to maintain a stable reference to a contact, even if their underlying cryptographic keys change.
    - **Crucially, a `PeerId` MUST NEVER be transmitted to other peers or computed from cryptographic material.**

- **`identity_agreement_key` is the Public Lookup Key**: A peer's public identity is defined by their cryptographic keys. When initiating a session with a peer for the first time, the public `identity_agreement_key` from their `PreKeyBundle` is the canonical value used to look them up. This key is the foundation of their cryptographic identity.

This clear separation ensures that local application logic (managing a contact list) is decoupled from the security-critical operations of session establishment, which are based purely on cryptography.

---

## 1. Public Transport Layer Contracts

These contracts define the public-facing gRPC services that are exposed to other peers on the network.

### `messaging.proto`

This service is the primary gateway for all secure communication.

-   **Session Management**: Provides RPCs to establish encrypted direct (one-to-one) and group (many-to-many) message sessions, based on the underlying `Percolator.Cryptography` domain.
-   **Opaque Message Exchange**: Once a session is established, this service's primary role is to accept and forward opaque, encrypted byte payloads. It has zero knowledge of the content of these payloads.

### `file_transfer.proto`

This is a secondary, specialized service for high-throughput data transfer.

-   **Chunk Streaming**: Provides a single RPC for peers to stream file chunks (identified by their hash) to one another.
-   **Security**: This endpoint is inherently untrusted. The application layer is responsible for validating incoming stream requests and can reject unauthorized streams.

---

## 2. Internal Application Layer Contracts

These contracts define the structure of messages *after* they have been decrypted. They are serialized and sent inside the opaque payloads of the public `messaging.proto` service.

### `internal_messaging.proto`

This file defines the core `InternalEnvelope` used by the application's internal message bus (Mediator).

-   **Two-Level Dispatch**: All internal messages are wrapped in a top-level `InternalEnvelope`. This envelope uses a `oneof` field to route the payload to the correct application-specific envelope (e.g., `ChatEnvelope`, `DhtEnvelope`). This second-level envelope then uses its own `oneof` to dispatch to a specific message handler. This provides a highly scalable and type-safe routing mechanism for features like chat, file sharing, and peer discovery.

---

## Important Note on PeerId

A `PeerId` is a **local-only, non-cryptographic identifier**. It is randomly generated (as a GUID) and is used to uniquely identify a peer within the local application instance.

**Key Principles:**
-   **Local Scope:** A `PeerId` is only meaningful to the local application. It is never shared with remote peers.
-   **Not for Authentication:** It MUST NOT be used for authentication or as a security credential. All security operations (like session management) are tied to cryptographic keys, not the `PeerId`.
-   **Stable Identifier:** It allows the application to maintain a stable reference to a peer, even if that peer's underlying cryptographic keys change.

This rule is enforced across all projects in the solution to ensure a clear and secure identity model.

## Protocol Design Principles

To ensure robustness and future compatibility, all public-facing contracts adhere to the following principles:

-   **Versioning**: All request messages include a `uint32 version` field. This allows for smooth protocol upgrades over time.
-   **Rate-Limiting**: All response messages include an `optional google.protobuf.Timestamp retry_after_utc` field. This provides a standard mechanism for a peer to inform a client that it is being rate-limited and when it is safe to retry the request.
-   **Compatibility**: All fields must be marked as `optional`. This provides a presence-checking capability (e.g., the `Has...()` methods in C# for scalar types) and prevents deserialization errors if a field is missing.
    -   The application is responsible for ensuring that all required fields are present in the request message.

## AI Assistant Guidance

-   **Contracts Only**: This project must only contain `.proto` files. Do not add C# code, build logic, or any other artifacts.
-   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (message types, services, fields) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "message MyMessage"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a message name proves false, the solution is *never* to create an empty file or message with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* message that should be used.
