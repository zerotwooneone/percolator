# Percolator.Application

This project is the **Application Layer** of the Percolator system. It is responsible for orchestrating the domain models and infrastructure services to execute the application's use cases.

## Guiding Principles

- **Orchestration, Not Logic**: The primary role of this layer is to orchestrate. It coordinates the domain objects from `Percolator.Messaging`, `Percolator.Identity`, etc., to perform tasks. It should contain minimal business logic itself; all business rules are delegated to the domain models.
- **Use Case Driven**: The services defined here (e.g., `IMessageService`, `IConversationService`) represent the concrete use cases and features of the application.
- **Thin Services**: Application services should be kept "thin," acting as a facade over the rich domain models.

## Key Responsibilities

1.  **Public API Implementation**: This project contains the implementations of the public-facing gRPC services (e.g., `MessagingGrpcService`). These services receive requests from the network, pass the encrypted data to the cryptography domain, and then hand the decrypted payload to the internal dispatcher.

2.  **Internal Message Dispatching**: A core component of this layer is the `InternalMessageMediator`. This service implements the Mediator pattern to act as an in-memory message bus. It inspects decrypted message envelopes and routes them to the correct, registered handler for processing (e.g., routing a `TextMessage` to the `TextMessageHandler`).

3.  **Dependency Injection**: This layer is responsible for wiring up all the application's components—services, repositories, and domain models—in the dependency injection container.

## Boundaries

- **Entry Point**: It serves as the main entry point for external requests into the system's core logic.
- **Dependencies**: It depends on the various Domain Libraries (`Percolator.Messaging`, `Percolator.Cryptography`, etc.) and the `Percolator.Contracts` project. It is the central hub that connects all other pieces of the system.

## Platform Dependencies

### Windows Only

This library has a hard dependency on the Windows operating system. This is due to the `CredentialService` which uses the **Windows Data Protection API (DPAPI)** via `System.Security.Cryptography.ProtectedData` to securely encrypt and store the password for the identity certificate.

This design decision was made to avoid storing sensitive credentials in plaintext or hardcoded in the source code. Future work may involve abstracting this service to support other platforms (e.g., using macOS Keychain or Linux Secret Service).

## Architectural Patterns

### CQRS with MediatR

This layer uses the **MediatR** library to implement the Command Query Responsibility Segregation (CQRS) pattern. This keeps the orchestration logic clean, decoupled, and easy to test.

-   **Commands**: Represent an intention to change the state of the system (e.g., `CreateManifestCommand`). They are handled by a single handler and should not return data.
-   **Queries**: Represent a request for data (e.g., `GetPeerListQuery`). They are handled by a single handler and must not change state.
-   **Notifications**: Represent a domain event that has occurred (e.g., `PeerDiscoveredNotification`). They can be handled by multiple handlers and are used to trigger side effects across different parts of the application. This is how the application layer responds to domain-level communication (e.g., implementing `IPeerDiscoveryHandler` to publish notifications).

### Error Handling and Exception Prevention

This application layer serves as a security boundary. It is responsible for validating data and performing sanity checks **before** passing requests to the domain layers (`Percolator.Network`, `Percolator.Cryptography`). This includes enforcing rate limits on incoming requests.

The domain layers operate on a "fail forward" policy and will throw exceptions on any data that violates their contracts. The application layer's primary error handling duty is to prevent these exceptions from occurring under normal conditions by rigorously validating all inputs. This ensures that domain-level exceptions represent true, unexpected security or logic violations, not routine validation failures.

### Session Reset Handling: Archive and Replace

When a peer initiates a new secure session handshake for a conversation that already exists (e.g., after a device reinstall or state loss), the system must handle the reset gracefully to balance security and user experience. This system follows the "Archive and Replace" model:

1.  **Archive Existing Conversation**: The current `DirectConversation` is marked as `Archived`. Its associated cryptographic session is destroyed, rendering its message history securely unreadable going forward. The message history itself is preserved in a read-only state for the local user's reference.
2.  **Create New Conversation**: A new `DirectConversation` is created with a new, unique `ConversationId`.
3.  **Establish New Session**: A new cryptographic session (e.g., Double Ratchet) is established and linked exclusively to the new conversation.

This approach ensures that forward secrecy is maintained by cleanly separating cryptographic sessions while preventing data loss for the user.

### Development Guidelines

- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

## Important Note on PeerId

A `PeerId` is a **local-only, non-cryptographic identifier**. It is randomly generated (as a GUID) and is used to uniquely identify a peer within the local application instance.

**Key Principles:**
-   **Local Scope:** A `PeerId` is only meaningful to the local application. It is never shared with remote peers.
-   **Not for Authentication:** It MUST NOT be used for authentication or as a security credential. All security operations (like session management) are tied to cryptographic keys, not the `PeerId`.
-   **Stable Identifier:** It allows the application to maintain a stable reference to a peer, even if that peer's underlying cryptographic keys change.

This rule is enforced across all projects in the solution to ensure a clear and secure identity model.
