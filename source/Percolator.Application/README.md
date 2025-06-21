# Percolator.Application

This project serves as the central application layer for the Percolator system.

## Purpose

The primary responsibility of the `Application` layer is to orchestrate business logic and coordinate tasks between the various domain libraries (e.g., `Percolator.Network`, `Percolator.Cryptography`, `Percolator.Contracts`). It acts as the "glue" that holds the system together, ensuring that domain models remain pure and decoupled from one another.

### Key Responsibilities:

-   **Service Implementation**: Contains implementations of services, such as the `FileSharingService` for gRPC.
-   **Orchestration**: Manages the flow of data and calls between different parts of the system. For example, it would handle receiving a manifest announcement, verifying its signature using the `Cryptography` library, and storing it.
-   **Dependency Injection**: Wires up dependencies for the main executable (`Percolator.Node`).
