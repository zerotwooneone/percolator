# Percolator.Cryptography

This project is a core domain library for the Percolator chat application. It is responsible for all cryptographic operations required to establish and maintain secure, end-to-end encrypted communication sessions.

## Goal

The primary goal of this library is to provide a self-contained, secure, and well-tested implementation of the cryptographic protocols needed for Percolator. It encapsulates the complexity of modern cryptographic systems, offering a simple API to the application layer for encrypting and decrypting messages.

This library is designed with Domain-Driven Design (DDD) principles in mind. It contains only pure cryptographic logic and is completely isolated from any infrastructure concerns like networking, databases, or user interfaces.

## High-Level Concepts

The security of the chat application is built upon several key cryptographic concepts implemented in this library:

-   **One-to-One Messaging (Double Ratchet)**: At the core of our session management is the Signal Double Ratchet algorithm. This provides exceptional security properties for two-party conversations, including:
    -   **Forward Secrecy**: If a user's long-term keys are compromised, past messages cannot be decrypted.
    -   **Future Secrecy (or Post-Compromise Security)**: If a user's keys are compromised, the session can "heal" itself, and future messages will once again be secure after a few message exchanges.

-   **Group Messaging (Sender Keys)**: To support secure and efficient group chats, the library will implement a "sender keys" or multicast encryption protocol. Each member of a group will use the pairwise Double Ratchet channel to securely receive a shared group key, which is then used to encrypt messages sent to the entire group.

-   **Distributed File System Cryptography**: To enable a secure, peer-to-peer file sharing network, the library will provide the cryptographic primitives for a distributed file system. This involves:
    -   **Content Encryption**: Each file is encrypted with its own unique symmetric key.
    -   **Access Control via Key Exchange**: Access to a file is granted by securely sharing its symmetric key with authorized peers using the established Double Ratchet or group messaging channel.
    -   **Hierarchical Permissions**: Directory structures can be managed by encrypting directory metadata and sharing keys in a hierarchical manner, allowing for access control to entire branches of the file system.
    -   **Data Integrity and Authenticity**: File contents will be hashed to ensure integrity, allowing peers to verify that the data they receive is correct and untampered.

-   **X3DH Key Agreement Protocol**: The "Extended Triple Diffie-Hellman" (X3DH) protocol is used to establish a secure, shared secret key between two users asynchronously. This is crucial for setting up an encrypted session without requiring both users to be online simultaneously. It involves:
    -   **Identity Keys**: Long-term keys that identify a user.
    -   **Signed Pre-keys**: Medium-term keys that are signed by the identity key.
    -   **One-Time Pre-keys**: A large batch of single-use keys for initial message exchanges.

-   **Cryptographic Primitives**: The library uses standard, well-vetted cryptographic primitives provided by .NET's `System.Security.Cryptography`:
    -   **AES-256 (CBC/GCM)**: For symmetric encryption of message content.
    -   **HMAC-SHA256**: For authenticating messages and deriving keys.
    -   **Curve25519 (or similar)**: For Elliptic Curve Diffie-Hellman (ECDH) key exchanges.

## Assumptions

The design and implementation of this library operate on the following assumptions:

1.  **Secure Pre-key Server**: The library assumes the existence of a trusted entity (e.g., a server or a DHT) that can securely store and distribute users' public pre-key bundles. The library is not responsible for the transport of these bundles.
2.  **Domain Purity**: This project will remain a pure domain library. It will not contain any references to UI frameworks, databases, network sockets, or other infrastructure-level concerns. Its dependencies will be minimal.
3.  **Reliable Primitives**: We trust that the underlying cryptographic primitives provided by the .NET Base Class Library (BCL) are implemented correctly and are secure against known attacks. We are not implementing our own primitives.
4.  **Out-of-Band Identity Verification**: This library does not handle the process of verifying a user's identity out-of-band (e.g., by comparing safety numbers or scanning QR codes). It assumes that the identity keys retrieved for a user are authentic.
