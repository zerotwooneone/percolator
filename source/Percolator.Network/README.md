# Percolator.Network

This library contains the networking logic for the Percolator file-sharing system. It is responsible for peer discovery, session management, and implementing the data transfer protocols.

## Architecture

The networking layer uses a hybrid model to balance efficiency and reliability for both local and internet-based peers:

- **Peer Discovery**:
  - **LAN**: On a local network, peers discover each other using UDP broadcast/multicast. This allows for zero-configuration discovery.
  - **Internet**: For peers across the internet, a DHT-based peer discovery mechanism is used.

## Core Responsibilities

- **DHT-based Peer Discovery**: Manages a simple, small-scale Distributed Hash Table (DHT) for discovering peers across the internet. The DHT is designed for networks of approximately 100 nodes or less, with a maximum of 3 hops.
- **Routing Table Management**: Maintains the state of the local DHT routing table. Nodes in the DHT are identified by the unique `Guid` from their `Percolator.Identity`.
- **Network Update Generation**: When its view of the network changes, this domain generates opaque "network update" messages. The actual transport of these messages is handled by the `Application` layer.
- **Defines Communication Interfaces**: Provides interfaces (e.g., `INetworkUpdatePublisher`) that the `Application` layer implements to distribute the network updates over a secure channel.
- **Data Transfer**: Implements the gRPC protocols for direct peer-to-peer bulk data transfer (e.g., for files).

## Architectural Integration

While this domain manages the logic of the DHT, it is not responsible for the transport of its own update messages. This is a critical separation of concerns:

1.  The `Network` domain generates an opaque update payload.
2.  The `Application` layer takes this payload and sends it as a secure, non-text message using the Double Ratchet session established by the `Cryptography` and `Messaging` domains.
3.  The `Application` layer also handles user-defined policies, such as ignoring updates from certain peers or respecting "back-off" requests, keeping the `Network` domain free of business logic.

## Design Goals

- **IPv6 First**: The networking stack is designed to be IPv6-first to ensure future compatibility. It will include a fallback to IPv4 to maintain support for older networks.

### Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., invalid cryptographic signatures, malformed packets). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.

## AI Assistant Guidance

1.  **Domain Independence**: This is a domain library. It **must not** contain direct references to other domain libraries (e.g., `Percolator.Cryptography`, `Percolator.Identity`). All cross-domain interactions must be handled through interfaces defined within this project. The `Percolator.Application` project is responsible for implementing these interfaces and orchestrating the interactions.
2.  **Fail Forward**: As detailed in the "Error Handling and Security" section, this project must not contain any logging for security-sensitive violations. It must 'fail forward' by throwing an appropriate exception (e.g., `SecurityException`) to be handled by the application layer.
3.  **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.

## Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.
