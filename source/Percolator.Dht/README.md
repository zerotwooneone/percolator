# Percolator.Dht

`Percolator.Dht` is a domain library that provides a Distributed Hash Table (DHT) for peer discovery within the Percolator network. It is designed as a modular application that runs on top of the secure communication channels established by `Percolator.Application`.

## Purpose

The primary goal of this library is to enable decentralized peer discovery. Instead of relying on centralized servers to find other nodes, peers can query the DHT to find the connection information (e.g., IP address and port) for other peers they wish to communicate with. 

## Architecture

The implementation is based on the Kademlia DHT algorithm. This provides an efficient and resilient way to maintain the network's routing table.

### Key Components

*   **`DhtEnvelope`**: A Protobuf message defined in `Percolator.Contracts` that encapsulates all DHT-related messages, such as `PingRequest`, `FindNodeRequest`, and their corresponding responses.
*   **MediatR Handlers**: Each DHT message type has a corresponding MediatR request and handler defined within this library. This follows the CQRS pattern and keeps the logic for each message type isolated and testable.
*   **`IDhtNodeRepository`**: An interface that defines the contract for storing and retrieving DHT node information. This allows the core domain logic to remain independent of the persistence mechanism.
*   **Node Information**: The repository will store essential information for peer discovery, including the peer's unique ID and their last known network address.

## Integration

`Percolator.Dht` is integrated into the main application in the following way:

1.  The `PercolatorMessageService` in `Percolator.Application` receives encrypted messages from other peers.
2.  After decryption, the service inspects the `InternalEnvelope`.
3.  If the envelope contains a `DhtEnvelope`, the service uses MediatR to dispatch the appropriate request (e.g., `PingRequest`) to its handler within this library.
4.  The handler processes the request, interacts with the `IDhtNodeRepository` if necessary, and returns a response.

This architecture ensures that the DHT logic is well-encapsulated and decoupled from the core transport and session management services.
