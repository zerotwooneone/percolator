# Percolator.Messaging

This project contains the domain logic for handling direct and group messaging within the Percolator network.

## Domain Responsibilities

- Defining the core models for messages, such as `DirectMessage` and `Group`.
- Managing the state and rules associated with messages (e.g., timestamps, author identity, content).
- Providing interfaces for message storage and retrieval, which will be implemented by other layers.

## AI Assistant Guidance

### 1. Strict Domain Independence

This is a domain library. It must **never** directly reference another domain library (e.g., `Percolator.Identity`, `Percolator.Cryptography`). All cross-domain interactions must be handled through interfaces defined within this project. The `Percolator.Application` project is responsible for implementing these interfaces and orchestrating the interactions.

For example, to associate a message with a sender's identity, this project should define an `IMessageSender` interface with properties like `Id` and `DisplayName`. The `Application` layer will then implement this interface, using the `Percolator.Identity` domain to provide the concrete data.

### 2. Fail Forward

This project must not contain any logging. If a rule is violated or an unrecoverable error occurs, the code must 'fail forward' by throwing an appropriate exception (e.g., `ArgumentException`, `InvalidOperationException`). The `Application` layer is responsible for catching these exceptions and handling them gracefully.
