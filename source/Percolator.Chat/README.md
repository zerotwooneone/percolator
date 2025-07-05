# Percolator.Chat

This project is the **Chat Domain Library** for the Percolator system. It is a self-contained, pure domain library responsible for managing all business logic related to chat functionality.

## Guiding Principles

- **Domain-Driven Design (DDD)**: This library is modeled as a distinct Bounded Context within the larger Percolator ecosystem. It contains the aggregate roots, entities, and value objects that define the chat domain.
- **Isolation**: It has no dependencies on the Application, Infrastructure, or Network layers. It is a portable library that contains only chat-related business rules.
- **Rich Domain Model**: The library will contain a rich, expressive domain model that encapsulates all chat-related logic, ensuring that the business rules are centralized, explicit, and easy to test.
- **Strongly-Typed IDs**: The domain will not expose primitive types like `Guid` or `byte[]` on its public interfaces. Instead, it will wrap them in strongly-typed value objects (e.g., `ParticipantId` instead of `Guid`) to improve type safety and code clarity.

## Scope and Features

This library will provide the domain models to support the following features:

- **One-on-One and Group Conversations**: The core `Conversation` aggregate will manage the state for both direct messages and multi-participant group chats.
- **Text Messaging**: The `Message` entity will represent a single message within a conversation.
- **Emoji Reactions**: The model will support adding and removing emoji reactions to messages.
- **Read Receipts**: The model will track which participants have read which messages.

## Boundaries

- **Entry Point**: The `Percolator.Application` layer will orchestrate this domain model, calling its methods to execute use cases like sending a message or creating a group.
- **Persistence**: The domain defines repository interfaces (e.g., `IConversationRepository`), but the implementation of these interfaces resides in the Infrastructure layer.
- **Security**: This library is not responsible for encryption or authentication. It operates on the assumption that it is being orchestrated by a secure application layer that has already handled decryption and authenticated the user.
