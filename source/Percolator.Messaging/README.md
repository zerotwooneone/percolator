# Percolator.Messaging

This project contains the domain logic for handling direct and group messaging within the Percolator network.

## Domain Responsibilities

- **Manages the Social Graph**: Defines and manages the core entities of `Peer`, `DirectConversation`, and `GroupConversation`. These entities are identified by stable, unique `Guid`s to decouple them from underlying cryptographic keys which may rotate.
- **Conversation Lifecycle**: Manages the state of a `DirectConversation` using a state machine (e.g., `Establishing`, `Active`, `Terminated`). It enforces rules based on this state, such as only allowing setup messages during the `Establishing` phase. `GroupConversation` entities are stateless within this domain.
- **Opaque Message Handling**: Stores and sequences messages (`DirectMessage`, `GroupMessage`), but treats their content as opaque data blobs (`byte[]`). The domain does not interpret message content, whether it is for session setup, plain text, or an internal gRPC request.
- **Trusted Intermediary Awareness**: The domain model supports the concept of routing messages through a trusted intermediary `Peer`, but the implementation of this routing logic resides in the `Application` layer.
- **Defines Persistence Interfaces**: Provides interfaces for storing and retrieving messaging entities, which are implemented by other layers.

## Core Concepts

- **Peer**: Represents a unique contact, identified by a `Guid`. To this domain, a peer is simply an ID and an optional nickname.
- **DirectConversation**: A stateful, one-to-one interaction between two peers. It begins in an `Establishing` state and transitions to `Active` once the secure channel is confirmed by the `Application` layer.
- **GroupConversation**: A stateless container for messages between a defined set of peers. It is identified by a `Guid` that directly corresponds to a cryptographic group managed by the `Percolator.Cryptography` domain.
- **DirectMessage / GroupMessage**: A piece of opaque data exchanged within a conversation, associated with a sender and a timestamp. Using distinct types makes the domain language clearer.

## AI Assistant Guidance

### 1. Strict Domain Independence

This is a domain library. It must **never** directly reference another domain library (e.g., `Percolator.Identity`, `Percolator.Cryptography`). All cross-domain interactions must be handled through interfaces defined within this project. The `Percolator.Application` project is responsible for implementing these interfaces and orchestrating the interactions.

For example, to associate a message with a sender's identity, this project should define an `IMessageSender` interface with properties like `Id` and `DisplayName`. The `Application` layer will then implement this interface, using the `Percolator.Identity` domain to provide the concrete data.

### 2. Fail Forward

This project must not contain any logging. If a rule is violated or an unrecoverable error occurs, the code must 'fail forward' by throwing an appropriate exception (e.g., `ArgumentException`, `InvalidOperationException`). The `Application` layer is responsible for catching these exceptions and handling them gracefully.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.
