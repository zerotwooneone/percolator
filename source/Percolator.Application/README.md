# Percolator.Application

This project is the **Application Layer** of the Percolator system. It is responsible for orchestrating the domain models and infrastructure services to execute the application's use cases.

## Guiding Principles

- **Orchestration, Not Logic**: The primary role of this layer is to orchestrate. It coordinates the domain objects from `Percolator.Cryptography`, `Percolator.Identity`, etc., to perform tasks. It should contain minimal business logic itself; all business rules are delegated to the domain models.
- **Use Case Driven**: The services defined here (e.g., `DirectSessionManager`) represent the concrete use cases and features of the application.
- **Thin Services**: Application services should be kept "thin," acting as a facade over the rich domain models.

## Security Posture

This application layer is a hardened security boundary designed to protect the underlying domain logic. It implements several key security controls:

- **Secure Session Establishment**: The primary network entry point, `PercolatorMessageService`, enforces a strict security model for session creation. It generates a new, random, and unique `PeerId` for every incoming session request, preventing session collision and hijacking attacks where a malicious client could attempt to control its identifier.
- **Resilience to Race Conditions**: The `DirectSessionManager` implements a per-conversation locking mechanism (`SemaphoreSlim`) to serialize message processing. This prevents race conditions where concurrent messages could corrupt the Double Ratchet state, ensuring session integrity and preventing denial-of-service attacks.
- **Input Validation**: All incoming requests are rigorously validated before being passed to domain services. This includes enforcing rate limits to protect against resource exhaustion attacks.

## Key Responsibilities

1.  **gRPC Service Implementation**: This project contains `PercolatorMessageService`, the implementation of the public-facing gRPC transport service. It serves as the primary entry point for all remote communication. It is responsible for receiving requests, orchestrating the X3DH handshake via `X3DHOrchestrator`, and establishing secure sessions with `DirectSessionManager`.

2.  **Session and Message Management**: It manages the lifecycle of cryptographic sessions (`DirectSessionManager`) and handles the routing and processing of decrypted messages.

3.  **Dependency Injection**: This layer is responsible for wiring up all the application's components—services, repositories, and domain models—in the dependency injection container.

## Boundaries

- **Entry Point**: It serves as the main entry point for external requests into the system's core logic.
- **Dependencies**: It depends on the various Domain Libraries (`Percolator.Cryptography`, `Percolator.Identity`, etc.) and the `Percolator.Contracts` project. It is the central hub that connects all other pieces of the system.

## Platform Dependencies

### Windows Only

This library has a hard dependency on the Windows operating system. This is due to the `CredentialService` (located in the `Percolator.Identity` project) which uses the **Windows Data Protection API (DPAPI)** via `System.Security.Cryptography.ProtectedData` to securely encrypt and store the password for the identity certificate.

This design decision was made to avoid storing sensitive credentials in plaintext or hardcoded in the source code. Future work may involve abstracting this service to support other platforms (e.g., using macOS Keychain or Linux Secret Service).

## Error Handling and Exception Prevention

This application layer serves as a security boundary. It is responsible for validating data and performing sanity checks **before** passing requests to the domain layers (`Percolator.Cryptography`, `Percolator.Identity`). This includes enforcing rate limits on incoming requests.

The domain layers operate on a "fail forward" policy and will throw exceptions on any data that violates their contracts. The application layer's primary error handling duty is to prevent these exceptions from occurring under normal conditions by rigorously validating all inputs. This ensures that domain-level exceptions represent true, unexpected security or logic violations, not routine validation failures. The per-conversation lock in `DirectSessionManager` is a key part of this strategy, preventing state corruption from concurrent requests.

### Session Reset Handling: Archive and Replace

When a peer initiates a new secure session handshake for a conversation that already exists (e.g., after a device reinstall or state loss), the system must handle the reset gracefully to balance security and user experience. This system follows the "Archive and Replace" model:

1.  **Archive Existing Conversation**: The current `DirectConversation` is marked as `Archived`. Its associated cryptographic session is destroyed, rendering its message history securely unreadable going forward. The message history itself is preserved in a read-only state for the local user's reference.
2.  **Create New Conversation**: A new `DirectConversation` is created with a new, unique `ConversationId`.
3.  **Establish New Session**: A new cryptographic session (e.g., Double Ratchet) is established and linked exclusively to the new conversation.

This approach ensures that forward secrecy is maintained by cleanly separating cryptographic sessions while preventing data loss for the user.

### Development Guidelines

- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

---

## Peer Identification and `PeerId`

In Percolator's peer-to-peer model, identity is handled with a simple and secure approach that cleanly separates local application identifiers from cryptographic identifiers.

- **`PeerId` is a Local-Only Identifier**: A `PeerId` is a non-cryptographic identifier (e.g., a GUID) used to uniquely reference a peer *within the local application instance only*. It allows the application to maintain a stable reference to a contact, even if their underlying cryptographic keys change.
    - **Crucially, a `PeerId` MUST NEVER be transmitted to other peers or computed from cryptographic material.**

- **`identity_agreement_key` is the Public Lookup Key**: A peer's public identity is defined by their cryptographic keys. When initiating a session with a peer for the first time, the public `identity_agreement_key` from their `PreKeyBundle` is the canonical value used to look them up. This key is the foundation of their cryptographic identity.

This clear separation ensures that local application logic (managing a contact list) is decoupled from the security-critical operations of session establishment, which are based purely on cryptography.

---

*   **`CredentialService`**: (Note: This service is located in the `Percolator.Identity` project). It manages the secure storage and retrieval of the user's cryptographic identity, using Windows DPAPI with additional entropy for protection.
*   **`DirectSessionManager`**: Manages the lifecycle of direct peer-to-peer conversations. It uses `SemaphoreSlim` to enforce per-conversation locking, preventing race conditions during message processing.
*   **`PeerConnectionManager`**: Manages gRPC connections to other peers.
