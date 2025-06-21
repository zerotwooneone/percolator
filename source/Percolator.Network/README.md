# Percolator.Network

This library contains the networking logic for the Percolator file-sharing system. It is responsible for peer discovery, session management, and implementing the data transfer protocols.

## Architecture

The networking layer uses a hybrid model to balance efficiency and reliability for both local and internet-based peers:

- **Peer Discovery**:
  - **LAN**: On a local network, peers discover each other using UDP broadcast/multicast. This allows for zero-configuration discovery.
  - **Internet**: For peers across the internet, a different mechanism (e.g., a DHT, tracker, or bootstrap node list) will be implemented.

- **Data Transfer**:
  - All core data exchange is handled via **gRPC** (running over TCP). This provides a reliable, high-performance, and strongly-typed RPC framework that integrates seamlessly with our Protobuf data models.
  - The gRPC services handle:
    - Announcing and requesting manifests.
    - Transferring file chunks using server-side streaming for efficiency.

## Design Goals

- **IPv6 First**: The networking stack is designed to be IPv6-first to ensure future compatibility. It will include a fallback to IPv4 to maintain support for older networks.

### Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., invalid cryptographic signatures, malformed packets). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.
