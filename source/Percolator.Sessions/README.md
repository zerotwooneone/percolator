# Percolator.Sessions

This project is a core **Domain Library** in the Percolator system. It is responsible for modeling the concepts and enforcing the business rules related to managing secure communication sessions.

## Guiding Principles

- **Rich Domain Models**: This library contains the rich domain models for sessions, including Aggregates and Value Objects. Key examples include `DirectConversation`, `DirectMessage`, `ConversationId`, and `PeerId`.
- **Business Logic**: All core business logic and invariants related to sessions reside here. For example, the rule that a message must belong to a valid conversation is enforced by the domain models themselves.
- **Persistence Ignorance**: The domain models have no knowledge of how they are stored. The library is completely independent of any database or persistence technology.
- **Transport Ignorance**: The library is unaware of how messages are received or sent over the network. It is a pure C# library with no dependencies on gRPC or other transport mechanisms.

## Boundaries

- **Depends on Nothing**: This project does not depend on any other domain or application library within the solution.
- **Used by Application Layer**: The services and models in this library are orchestrated by the `Percolator.Application` layer to execute specific use cases.

## Domain Responsibilities

- **Manages Conversation State**: Defines and manages the core entities of `DirectConversation` and `GroupConversation`. These entities are identified by stable, unique `Guid`s wrapped in strongly-typed IDs.
- **Conversation Lifecycle**: Manages the state of a `DirectConversation` using a state machine (e.g., `Establishing`, `Active`, `Terminated`). It enforces rules based on this state. `GroupConversation` entities are stateless within this domain.
- **Opaque Message Handling**: Stores and sequences messages (`DirectMessage`, `GroupMessage`), but treats their content as opaque data blobs (`byte[]`). The domain does not interpret message content. Application-level concepts like text messages, read receipts, or file manifests are handled by the `Percolator.Application` layer.
- **Defines Persistence Interfaces**: Provides interfaces (e.g., `IMessageStore`) for storing and retrieving session-related entities, which are implemented by other layers.

## Core Concepts

- **PeerId**: Represents a unique contact, identified by a `Guid`. To this domain, a peer is simply an ID.
- **DirectConversation**: A stateful, one-to-one interaction between two peer IDs. It begins in an `Establishing` state and transitions to `Active` once the secure channel is confirmed by the `Application` layer.
- **GroupConversation**: A stateless container for messages between a defined set of peer IDs. It is identified by a `Guid` that directly corresponds to a cryptographic group managed by the `Percolator.Cryptography` domain.
- **DirectMessage / GroupMessage**: A piece of opaque data exchanged within a conversation, associated with a sender and a timestamp. Using distinct types makes the domain language clearer.

## AI Assistant Guidance

### 1. Strict Domain Independence

This is a domain library. It must **never** directly reference another domain library (e.g., `Percolator.Identity`, `Percolator.Cryptography`). All cross-domain interactions must be handled through interfaces defined within this project. The `Percolator.Application` project is responsible for implementing these interfaces and orchestrating the interactions.

### 2. Fail Forward

This project must not contain any logging. If a rule is violated or an unrecoverable error occurs, the code must 'fail forward' by throwing an appropriate exception (e.g., `ArgumentException`, `InvalidOperationException`). The `Application` layer is responsible for catching these exceptions and handling them gracefully.

### 3. Principle of Verification: Verify Before Acting

To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.

*   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
*   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
*   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
*   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.
