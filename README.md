# Percolator: A Secure, Decentralized Chat Application

## Overview

Percolator is an innovative chat application designed with a strong emphasis on security, decentralization, and a robust Domain-Driven Design (DDD) architecture. It leverages advanced cryptographic protocols and a Distributed Hash Table (DHT) for peer discovery, aiming to provide a secure and resilient communication platform. The project is built using C# .NET 9 and showcases a clear separation of concerns through distinct domain libraries.

## Key Features

- **Secure One-to-One & Group Messaging**: Implements state-of-the-art cryptographic protocols (Double Ratchet and Sender Keys) for end-to-end encrypted pairwise and group conversations.
- **Peer-to-Peer Distributed File System**: A decentralized file sharing system allowing users to grant access to their files and directories, with peers being able to share-alike the content they have access to.
- **Decentralized Peer Discovery**: Utilizes a custom Distributed Hash Table (DHT) for peer discovery, eliminating the need for central servers for user presence.
- **Domain-Driven Design (DDD)**: A layered architecture with clearly defined Bounded Contexts to manage complexity and maintain a clean domain model.
- **Multiple Client Applications**:
  - **Console Application**: Serves as a lightweight peer, capable of bootstrapping the DHT network and acting as a foundational node.
  - **WPF Desktop Application**: Provides a rich graphical user interface for chat interactions.
- **Modern .NET Development**: Built on .NET 9, leveraging features like `System.Text.Json` source generation for high performance.
- **CQRS with MediatR**: The application layer uses MediatR for clear command and query separation.
- **Reactive UI with R3**: The WPF application utilizes the R3 library for `ReactiveProperty<T>` and `ReadOnlyReactiveProperty<T>`, enabling reactive patterns for state management and UI binding.

## Architecture

Percolator's architecture is structured around several distinct projects, each representing a specific bounded context or layer.

### Core Domain Libraries (Business Logic Only)

These projects contain the pure domain logic and are designed to be highly cohesive and loosely coupled. They depend only on the absolute minimum necessary libraries to perform their core business functions. There are no shared utility libraries between these domains; any common types (like a custom `UserId` value object) are duplicated and exist independently within each relevant domain, or explicit mapping is performed at the application layer boundary. Each domain is self-contained and comes with its own dedicated unit tests.

#### Percolator.Messaging
- **Responsibility**: Manages chat conversations, message content, message history, read statuses, and participants within a conversation.
- **Key Concepts**: `Message`, `Conversation`, `Participant`, `ConversationRepository`.
- **Dependencies**: Minimal (e.g., `System.Collections.Generic`, `System.DateTime`).

#### Percolator.Cryptography
- **Responsibility**: Implements and manages the Signal Double Ratchet algorithm, key exchanges (e.g., X3DH), and cryptographic operations (encryption, decryption, signature verification).
- **Key Concepts**: `DoubleRatchetSession`, `RsaPublicKey`, `PrekeyBundle`, `KeyExchangeService`.
- **Dependencies**: Minimal (e.g., `System.Security.Cryptography`, `System.Buffers`).

#### Percolator.Network
- **Responsibility**: Handles the Distributed Hash Table (DHT) for peer discovery, node management, and low-level network communication (UDP).
- **Key Concepts**: `DhtNode`, `PeerAddress`, `RoutingTable`, `DhtService`.
- **Dependencies**: Minimal (e.g., `System.Net.Sockets`, `System.Collections.Concurrent`).

## Architectural Notes

- **Domain-Driven Design (DDD)**: The solution is organized into distinct domain libraries to promote separation of concerns and maintainability.
- **Protocol Buffers (Protobuf)**: We use Protobuf for efficient, cross-platform data serialization for network messages and stored data structures. To ensure forward and backward compatibility, all Protobuf messages must adhere to the following rules:
    1.  **Top-Level Version Field**: Every top-level message schema must include an `optional uint32 version` field. This allows consuming code to handle different message formats gracefully.
    2.  **Use `optional` Fields**: All data fields within a message should be marked as `optional`. This provides presence-checking capabilities (e.g., the `Has...()` methods in C# for scalar types) and prevents deserialization errors if a field is missing.

    > **Note for AI/Developers**: This rule is critical for maintaining a stable API in a distributed system. Always enforce this for new Protobuf schemas.
- **Inversion of Control for Domain Communication**: To maintain strict separation of concerns, domain libraries (e.g., `Percolator.Network`) must not directly raise events (e.g., C# `event`). Instead, they should define an interface (e.g., `IPeerDiscoveryHandler`) that represents the actions to be taken. The domain service will call methods on this interface, inverting the control. The concrete implementation of this interface resides in the application layer (`Percolator.Application`), which then uses a mediator (like MediatR) to dispatch notifications. This ensures that domain logic remains pure and decoupled from application-specific workflows.
- **Protobuf in C# Gotcha**: When checking for the presence of an `optional` field that is another message type (not a scalar like `int32` or `string`), the C# Protobuf generator does not create a `Has...()` method. Instead, you must check if the property is `null`.
- **Hybrid Networking Model**: The system is designed for both local (LAN) and internet-based peer-to-peer communication. It uses a hybrid approach:
    - **gRPC (over TCP)** is used for all reliable, stateful communication, such as announcing and transferring manifests and file chunks.
    - **UDP Broadcast/Multicast** is used for efficient, zero-configuration peer discovery on the local network.

## AI Collaboration Guidance

This section contains notes and guidelines for collaborating with AI assistants (like Cascade) on this project.

- **Protobuf C# Implementation Details**:
  - When checking for the presence of an `optional` field in a Protobuf message using C#, the method depends on the field's type:
    - For **scalar types** (e.g., `int32`, `string`, `bytes`, `bool`, `enum`), the compiler generates a `bool Has...` property. Always use this for presence checks (e.g., `if (message.HasMyField)`).
    - For **message types** (e.g., a field that is another message, like `google.protobuf.Timestamp`), the compiler does **not** generate a `Has...` property. To check for presence, you must compare the property to `null` (e.g., `if (message.MyNestedMessage != null)`).

## Getting Started

To get a local copy up and running, follow these simple steps.

### Prerequisites

- .NET 9 SDK or later

### Installation & Running

1. Clone the repo:
   ```sh
   git clone https://github.com/your_username/percolator.git
   ```
2. Navigate to the source directory:
   ```sh
   cd percolator/source
   ```
3. Build the solution:
   ```sh
   dotnet build
   ```
4. Run the desired application:
   - **Desktop App**:
     ```sh
     dotnet run --project Percolator.Desktop
     ```
   - **Console App**:
     ```sh
     dotnet run --project Percolator.Console
     ```

## License

Distributed under the MIT License. See `LICENSE` for more information.