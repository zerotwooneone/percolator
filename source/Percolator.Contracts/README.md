# Percolator.Contracts

This project contains the formal data contracts for the Percolator system, defined exclusively using Protocol Buffers (`.proto` files). It serves as the single source of truth for the structure of data that is serialized and transmitted between different parts of the system.

## Core Principles

- **Contracts Only**: This project MUST NOT contain any C# code, business logic, or helper classes. Its sole responsibility is to define data structures.
- **DDD Alignment**: By strictly defining our data contracts here, we create a clear boundary between the application's data structures and its domain logic.

## Architectural Goals

The system is designed with a two-layer architecture, which is reflected in the contracts:

1.  **Public Transport Layer**: A minimal, secure-by-default public interface. Its only job is to establish secure sessions and move opaque, encrypted data between peers.
2.  **Internal Application Layer**: A rich, feature-driven layer that operates exclusively over the secure channels established by the transport layer.

---

## 1. Public Transport Layer Contracts

These contracts define the public-facing gRPC services that are exposed to other peers on the network.

### `messaging.proto`

This service is the primary gateway for all secure communication.

-   **Session Management**: Provides RPCs to establish encrypted direct (one-to-one) and group (many-to-many) message sessions, based on the underlying `Percolator.Cryptography` domain.
-   **Opaque Message Exchange**: Once a session is established, this service's primary role is to accept and forward opaque, encrypted byte payloads. It has zero knowledge of the content of these payloads.
-   **Identity**: Session establishment is based on cryptographic keys. Peers can optionally include an additional long-term identity key to associate with their session.

### `file_transfer.proto`

This is a secondary, specialized service for high-throughput data transfer.

-   **Chunk Streaming**: Provides a single RPC for peers to stream file chunks (identified by their hash) to one another, similar to BitTorrent.
-   **Security**: This endpoint is inherently untrusted. The application layer is responsible for validating incoming stream requests (e.g., checking if the peer is authorized to send a specific chunk hash) and can reject unauthorized streams.

---

## 2. Internal Application Layer Contracts

These contracts define the structure of messages *after* they have been decrypted. They are serialized and sent inside the opaque payloads of the public `messaging.proto` service.

### `internal_messaging.proto`

This file defines the core `InternalEnvelope` used by the application's internal message bus (Mediator).

-   **Two-Level Dispatch**: All internal messages are wrapped in a top-level `InternalEnvelope`. This envelope uses a `oneof` field to route the payload to the correct application-specific envelope (e.g., `ChatEnvelope`, `DhtEnvelope`). This second-level envelope then uses its own `oneof` to dispatch to a specific message handler. This provides a highly scalable and type-safe routing mechanism.

### Application Features

The following features are built on top of the internal messaging system:

-   **Rich Text Chat**: A full-featured chat system including text messages, read receipts, and emoji annotations.
-   **Manifest-Based File Sharing**: A system for peers to discover and request files from each other via signed manifests.
-   **DHT Peer Discovery**: A simple, Kademlia-like Distributed Hash Table for discovering other peers on the network.
-   **Asynchronous Message Mailbox**: A store-and-forward mechanism to support offline messaging by hosting pre-key bundles and opaque messages for other peers.

---

## Protocol Design Principles

To ensure robustness and future compatibility, all public-facing contracts adhere to the following principles:

-   **Versioning**: All request messages include a `uint32 version` field. This allows for smooth protocol upgrades over time.
-   **Rate-Limiting**: All response messages include an `optional google.protobuf.Timestamp retry_after_utc` field. This provides a standard mechanism for a peer to inform a client that it is being rate-limited and when it is safe to retry the request.
