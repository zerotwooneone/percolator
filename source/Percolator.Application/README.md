# Percolator.Application

This project serves as the central application layer for the Percolator system.

## Purpose

The primary responsibility of the `Application` layer is to orchestrate business logic and coordinate tasks between the various domain libraries (e.g., `Percolator.Network`, `Percolator.Cryptography`, `Percolator.Contracts`). It acts as the "glue" that holds the system together, ensuring that domain models remain pure and decoupled from one another.

### Key Responsibilities:

-   **Service Implementation**: Contains implementations of services, such as the `FileSharingService` for gRPC, `ManifestService` for creating and managing file manifests, and services for managing identity and credentials.
-   **Secure Credential Management**: Implements `CredentialService` and `PersistentIdentityService` to securely store and manage user identity certificates and passwords using platform-native features.
-   **Orchestration**: Manages the flow of data and calls between different parts of the system. For example, it would handle receiving a manifest announcement, verifying its signature using the `Cryptography` library, and storing it.
-   **Dependency Injection**: Wires up dependencies for the main executable (`Percolator.Node`).

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

This application layer serves as a security boundary. It is responsible for validating data and performing sanity checks **before** passing requests to the domain layers (`Percolator.Network`, `Percolator.Cryptography`).

The domain layers operate on a "fail forward" policy and will throw exceptions on any data that violates their contracts. The application layer's primary error handling duty is to prevent these exceptions from occurring under normal conditions by rigorously validating all inputs. This ensures that domain-level exceptions represent true, unexpected security or logic violations, not routine validation failures.
