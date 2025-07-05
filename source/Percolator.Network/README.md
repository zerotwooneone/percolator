# Percolator.Network

This library contains the networking logic for the Percolator system. It is responsible for discovering peers on the local network using a secure, authenticated broadcast protocol.

## Architecture

The networking layer uses UDP broadcasts for zero-configuration peer discovery on a Local Area Network (LAN). All communication is authenticated using cryptographic signatures to ensure that only trusted nodes can participate.

## Core Responsibilities

-   **Secure Peer Discovery**: Manages the discovery of peers on the local network. It broadcasts the node's presence and listens for broadcasts from other peers.
-   **Authenticated Communication**: Ensures that all discovery messages are cryptographically signed and verified, preventing spoofing and unauthorized participation.
-   **Peer Lifecycle Management**: Tracks discovered peers, updates their status, and removes them when they expire.
-   **Defines Communication Interfaces**: Provides interfaces (e.g., `IPeerDiscoveryHandler`) that the `Application` layer implements to react to network events, such as a new peer being discovered or an existing one expiring.

## Complex Topics: The Secure Peer Discovery Protocol

This section provides a detailed breakdown of the most critical component in this library: the `PeerDiscoveryService`. It implements a secure protocol to ensure that all discovered peers are authentic.

### Protocol Overview

The service works by broadcasting a `DiscoveryBroadcast` message over UDP to the local network. This message is carefully constructed to be self-authenticating.

**Message Structure (`DiscoveryBroadcast`):**

1.  **`PublicKey`**: The sender's public *signing* key. This is sent in the clear and serves as the identifier for the sender.
2.  **`Payload`**: A byte array containing the core discovery information (e.g., the sender's gRPC port and a timestamp). This payload is what gets signed.
3.  **`Signature`**: The cryptographic signature of the `Payload`, created using the private key corresponding to the `PublicKey`.

### The Broadcast-and-Sign Process

When a node broadcasts its presence (`BroadcastPresenceAsync`):

1.  It creates a `DiscoveryPayload` containing its gRPC listening port and the current UTC timestamp.
2.  It serializes this payload to a byte array.
3.  It signs the serialized payload bytes using its long-term identity signing key.
4.  It constructs the final `DiscoveryBroadcast` message, including its public key, the signature, and the payload.
5.  This message is then broadcast to the LAN.

### The Listen-and-Verify Process

This is the most security-critical part of the protocol (`ListenForPeersAsync`):

1.  **Receive Broadcast**: A node receives a `DiscoveryBroadcast` UDP packet.
2.  **Verify Signature**: It takes the `Payload` bytes, the `Signature`, and the `PublicKey` from the message. It then uses the public key to verify that the signature is valid for the given payload. **If the signature is invalid, the packet is immediately discarded.** This is the primary defense against unauthorized nodes.
3.  **Verify Payload Integrity (Anti-Spoofing)**: This is a crucial second verification step. The `DiscoveryPayload` *inside* the signed payload also contains a copy of the sender's public key. The service deserializes the payload and verifies that the public key within it is identical to the public key from the outer `DiscoveryBroadcast` message. This prevents a replay or spoofing attack where a malicious actor could take a valid signed payload from one user and wrap it with their own public key.
4.  **Process Peer**: Only if both verification steps pass is the peer considered authentic. The service then adds the peer to its list of known peers and notifies the application layer.

```csharp
// Simplified example of the verification logic in ListenForPeersAsync

// 1. Parse the incoming broadcast
var broadcast = DiscoveryBroadcast.Parser.ParseFrom(result.Buffer);

// 2. Wrap primitives in value types for verification
var payload = new Payload(broadcast.Payload.ToByteArray());
var signature = new Signature(broadcast.Signature.ToByteArray());
var publicKey = new PublicKey(broadcast.PublicKey.ToByteArray());

// 3. Verify the signature (CRITICAL STEP 1)
if (!_signingService.Verify(payload, signature, publicKey))
{
    // Discard the packet
    throw new SecurityException("Invalid signature.");
}

// 4. Deserialize payload and verify the inner public key (CRITICAL STEP 2)
var protoPayload = DiscoveryPayload.Parser.ParseFrom(payload.Value);
if (!publicKey.Value.SequenceEqual(protoPayload.PublicKey.ToByteArray()))
{
    // Discard the packet
    throw new SecurityException("Public key mismatch (spoofing attempt).");
}

// 5. If both checks pass, the peer is authentic.
// ... process the peer ...
```

This two-step verification process ensures that the peer discovery mechanism is resilient against unauthorized access and tampering, forming a secure foundation for the rest of the application's network interactions.

## Important Note on PeerId

A `PeerId` is a **local-only, non-cryptographic identifier**. It is randomly generated (as a GUID) and is used to uniquely identify a peer within the local application instance.

**Key Principles:**
-   **Local Scope:** A `PeerId` is only meaningful to the local application. It is never shared with remote peers.
-   **Not for Authentication:** It MUST NOT be used for authentication or as a security credential. All security operations (like session management) are tied to cryptographic keys, not the `PeerId`.
-   **Stable Identifier:** It allows the application to maintain a stable reference to a peer, even if that peer's underlying cryptographic keys change.

This rule is enforced across all projects in the solution to ensure a clear and secure identity model.

**Note:** The `PeerId` plays a crucial role in managing peer connections locally but does not participate in the authentication or security verification process. Its primary function is to provide a stable identifier for peers within the local application context, ensuring that the application can maintain a consistent view of its connected peers despite changes in their cryptographic keys or other identifiers.
