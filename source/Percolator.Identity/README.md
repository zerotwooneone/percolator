# Percolator.Identity

This project is a domain library responsible for managing node identities within the Percolator network. It handles the creation, storage, and retrieval of cryptographic identities, primarily using X.509 certificates. This ensures that each node can be uniquely and securely identified.

## Core Responsibilities

-   **Manages the Root Identity**: Creates, stores, and retrieves a user's core cryptographic `Identity`. Each `Identity` is identified by a stable, unique `Guid`, which serves as the primary key for associating all user-related data across different domains.
-   **Source of Truth for Keys**: Acts as the authoritative source for a user's long-lived cryptographic keys, including signing keys, agreement keys, and their associated X.509 certificates.
-   **Provides Key Material**: Exposes interfaces that allow the `Application` layer to retrieve the necessary key material for other domains. For example, it provides the public key bundle required by the `Cryptography` domain to initiate a secure session, but it is not involved in the session protocol itself.

### Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., failure to create a certificate, inability to access storage). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.

## AI Assistant Guidance

When modifying this project, adhere to the following architectural rules:

1.  **Domain Independence**: This is a domain library. It **must not** contain direct references to other domain libraries (e.g., `Percolator.Cryptography`, `Percolator.Network`).
2.  **Interface-Based Dependencies**: If this domain requires functionality from another domain, it must define an interface (e.g., `ICertificateOperations`) that declares its needs. The `Percolator.Application` project is responsible for implementing this interface and orchestrating the interaction between domains.
3.  **Fail Forward**: Do not add logging for security-sensitive errors or validation failures. The established pattern is to throw an exception (e.g., `SecurityException`, `CryptographicException`) to ensure failures are handled by the application layer.

## Domain-Driven Design

This library uses DDD value objects to enforce type safety and clarify intent at the domain boundaries.

-   `Password`: Wraps a `string` to ensure passwords are not accidentally logged or mishandled as primitive types.
-   `Certificate`: Wraps an `X509Certificate2` object. The `IIdentityService.LoadIdentityAsync` method returns this object to provide the application layer with the necessary certificate for operations like signing, without exposing the raw PFX bytes or the password it was loaded with.
-   `Identity`: Represents the complete, stored identity, including its name, PFX data, and thumbprint.

### API Design

The primary entry point to this domain is `IIdentityService`. Its methods, such as `CreateIdentityAsync` and `LoadIdentityAsync`, intentionally do not require a password parameter. Password management is fully encapsulated within the domain and handled by the `ICredentialService`, which securely stores and retrieves the necessary credentials using the OS's Data Protection API (DPAPI). This design simplifies the public API and strengthens security by preventing password mishandling in the application layer.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.

### Development Guidelines

- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

## Future Considerations

*   **Restricted Identity Creation**: Currently, any service with access to `IIdentityService` can create an arbitrary number of identities. In a production environment, it may be necessary to introduce access controls or policies around identity creation. For now, this responsibility is delegated to the `Percolator.Application` layer, which should be the sole orchestrator of identity management.
