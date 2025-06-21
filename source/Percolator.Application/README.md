# Percolator.Application

This project serves as the central application layer for the Percolator system.

## Purpose

The primary responsibility of the `Application` layer is to orchestrate business logic and coordinate tasks between the various domain libraries (e.g., `Percolator.Network`, `Percolator.Cryptography`, `Percolator.Contracts`). It acts as the "glue" that holds the system together, ensuring that domain models remain pure and decoupled from one another.

### Key Responsibilities:

-   **Service Implementation**: Contains implementations of services, such as the `FileSharingService` for gRPC.
-   **Orchestration**: Manages the flow of data and calls between different parts of the system. For example, it would handle receiving a manifest announcement, verifying its signature using the `Cryptography` library, and storing it.
-   **Dependency Injection**: Wires up dependencies for the main executable (`Percolator.Node`).

## Architectural Patterns

### CQRS with MediatR

This layer uses the **MediatR** library to implement the Command Query Responsibility Segregation (CQRS) pattern. This keeps the orchestration logic clean, decoupled, and easy to test.

-   **Commands**: Represent an intention to change the state of the system (e.g., `CreateManifestCommand`). They are handled by a single handler and should not return data.
-   **Queries**: Represent a request for data (e.g., `GetPeerListQuery`). They are handled by a single handler and must not change state.
-   **Notifications**: Represent a domain event that has occurred (e.g., `PeerDiscoveredNotification`). They can be handled by multiple handlers and are used to trigger side effects across different parts of the application. This is how the application layer responds to domain-level communication (e.g., implementing `IPeerDiscoveryHandler` to publish notifications).
