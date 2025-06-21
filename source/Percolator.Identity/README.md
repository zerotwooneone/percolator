# Percolator.Identity

This project is a domain library responsible for managing node identities within the Percolator network. It handles the creation, storage, and retrieval of cryptographic identities, primarily using X.509 certificates. This ensures that each node can be uniquely and securely identified.

## Core Responsibilities

-   **Identity Generation**: Creates new cryptographic identities for the node.
-   **Identity Storage**: Manages the persistent storage of identity materials.
-   **Identity Retrieval**: Provides access to the node's identity for use in other domains, such as signing and verification.

### Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., failure to create a certificate, inability to access storage). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.

## AI Assistant Guidance

When modifying this project, adhere to the following architectural rules:

1.  **Domain Independence**: This is a domain library. It **must not** contain direct references to other domain libraries (e.g., `Percolator.Cryptography`, `Percolator.Network`).
2.  **Interface-Based Dependencies**: If this domain requires functionality from another domain, it must define an interface (e.g., `ICertificateOperations`) that declares its needs. The `Percolator.Application` project is responsible for implementing this interface and orchestrating the interaction between domains.
3.  **Fail Forward**: Do not add logging for security-sensitive errors or validation failures. The established pattern is to throw an exception (e.g., `SecurityException`, `CryptographicException`) to ensure failures are handled by the application layer.
