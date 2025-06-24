# Percolator.Application

This project serves as the central application layer for the Percolator system.

## Purpose

The primary responsibility of the `Application` layer is to orchestrate business logic and coordinate tasks between the various domain libraries (e.g., `Percolator.Network`, `Percolator.Cryptography`, `Percolator.Contracts`). It acts as the "glue" that holds the system together, ensuring that domain models remain pure and decoupled from one another.

### Key Responsibilities:

-   **Orchestration and ID Mapping**: Acts as the central coordinator, managing the flow of data between domains. It is responsible for mapping the stable `Guid` identifiers used in the `Messaging` and `Identity` domains to the session-specific data required by the `Cryptography` domain.
-   **Persistence Implementation**: Implements the persistence interfaces defined by the domain layers (e.g., `IDoubleRatchetStore`, `IMessageStore`). This keeps the domains pure and allows the application to manage all data storage.
-   **Business Rule Enforcement**: Enforces application-wide business rules that span multiple domains, such as limiting a user to one active direct messaging session per peer.
-   **Internal gRPC Service Hosting**: Hosts a non-network reachable, in-process gRPC service. It decrypts incoming secure messages and routes them to this internal service to handle sensitive operations like one-time key requests and file manifest sharing.
-   **File Sharing Workflow**: Manages the entire file sharing process. It handles requests for file manifests, enforces user-defined access policies, and authorizes direct peer-to-peer gRPC connections for the actual bulk file transfer.
-   **Service Implementation**: Contains implementations of services, such as the `FileSharingService` for gRPC, `ManifestService` for creating and managing file manifests, and services for managing identity and credentials.
-   **Secure Credential Management**: Implements `CredentialService` and `PersistentIdentityService` to securely store and manage user identity certificates and passwords using platform-native features.
-   **Dependency Injection**: Wires up dependencies for the main executable (`Percolator.Node`).
-   **`IManifestStore`**: Manages the persistent storage of manifests, enforcing size and count quotas to prevent DoS attacks.
-   **`ISharedDirectoryProvider`**: Provides a list of safe, pre-approved directories that can be shared. This is a security-critical component that prevents path traversal attacks.
-   **`IRateLimiter`**: Provides a mechanism to throttle requests from peers to prevent resource exhaustion.

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
