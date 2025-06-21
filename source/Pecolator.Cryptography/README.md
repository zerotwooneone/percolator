# Pecolator.Cryptography

This project is a core domain library for the Pecolator chat application. It is responsible for all cryptographic operations required to establish and maintain secure, end-to-end encrypted communication sessions.

## Goal

The primary goal of this library is to provide a self-contained, secure, and well-tested implementation of the cryptographic protocols needed for Pecolator. It encapsulates the complexity of modern cryptographic systems, offering a simple API to the application layer for encrypting and decrypting messages.

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

## Features

*   **X3DH (Extended Triple Diffie-Hellman) Protocol**: Securely establishes a shared secret key between two parties, even if the responder is offline. It provides authenticity through digital signatures (`ECDSA`).
*   **Double Ratchet Algorithm**: Provides ongoing secure communication with forward secrecy and post-compromise security.
*   **Authenticated Encryption**: Uses `AES-256-GCM` to ensure all messages are confidential, tamper-proof, and authentic.
*   **Resilience**: Handles out-of-order message delivery through a key caching mechanism.

## Key Classes

*   `X3DHManager`: Implements the X3DH handshake to establish an initial shared secret.
*   `DoubleRatchetSession`: Manages the ongoing stateful session, handling encryption and decryption of messages.
*   `PreKeyBundle`: A data structure representing a user's public keys needed for the X3DH handshake.
*   `RatchetMessage`: A data structure for transporting the ciphertext and the sender's ephemeral public key.

## Basic Usage Example

This example demonstrates how to establish and use a `DoubleRatchetSession` after two parties have already derived a shared secret using a key agreement protocol like X3DH.

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using Pecolator.Cryptography; // Use the namespace from your project

// 1. Setup: Pre-computation and shared secret
// In a real application, identity keys are long-term and stored securely.
// The sharedSecret would be the result of an X3DH handshake.
using var aliceIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
byte[] sharedSecret = new byte[32]; // Placeholder for X3DH result
RandomNumberGenerator.Fill(sharedSecret);

// 2. Session Initialization
// Bob, the responder, creates his session first.
using var bobSession = new DoubleRatchetSession(sharedSecret, bobIdentityKey, SessionRole.Responder);

// Alice, the initiator, needs Bob's initial ratchet public key to start the session.
// This key would typically be retrieved from a server as part of Bob's pre-key bundle.
byte[] bobRatchetPublicKey = bobSession.RatchetPublicKey;
using var aliceSession = new DoubleRatchetSession(sharedSecret, aliceIdentityKey, SessionRole.Initiator, bobRatchetPublicKey);

Console.WriteLine("Sessions initialized successfully.");

// 3. Alice sends the first message to Bob
string originalMessageFromAlice = "Hello Bob!";
RatchetMessage messageToBob = aliceSession.Encrypt(Encoding.UTF8.GetBytes(originalMessageFromAlice));

Console.WriteLine($"Alice sends: '{originalMessageFromAlice}'");

// 4. Bob decrypts the message from Alice
byte[] decryptedBytesFromAlice = bobSession.Decrypt(messageToBob);
string decryptedMessageForBob = Encoding.UTF8.GetString(decryptedBytesFromAlice);

Console.WriteLine($"Bob decrypts: '{decryptedMessageForBob}'");
if (originalMessageFromAlice == decryptedMessageForBob)
{
    Console.WriteLine("SUCCESS: Message decrypted correctly!");
}

// 5. Bob replies to Alice
string originalMessageFromBob = "Hello Alice, message received!";
RatchetMessage messageToAlice = bobSession.Encrypt(Encoding.UTF8.GetBytes(originalMessageFromBob));

Console.WriteLine($"Bob replies: '{originalMessageFromBob}'");

// 6. Alice decrypts the reply from Bob
byte[] decryptedBytesFromBob = aliceSession.Decrypt(messageToAlice);
string decryptedMessageForAlice = Encoding.UTF8.GetString(decryptedBytesFromBob);

Console.WriteLine($"Alice decrypts: '{decryptedMessageForAlice}'");
if (originalMessageFromBob == decryptedMessageForAlice)
{
    Console.WriteLine("SUCCESS: Reply decrypted correctly!");
}

// The 'using' statements ensure that the session objects and their ephemeral keys are properly disposed.
```

## Guidance for AI Assistants

*   **Two-Stage Protocol**: Understand that this is a two-part system. `X3DHManager` is used **once** at the beginning of a conversation to create a shared secret. This secret is then fed into the `DoubleRatchetSession` constructor to manage the ongoing conversation.
*   **Key Management**: The security of X3DH relies on a long-term identity key. This library assumes the key is provided; a real application must store this key securely on the client device.
*   **Server Role**: The X3DH protocol assumes a server exists to store and distribute public `PreKeyBundle`s. This server enables asynchronous communication but is not trusted; authenticity is guaranteed by the `ECDSA` signature in the bundle, which this library verifies.
*   **Encryption**: Encryption is handled by `AES-GCM`. Decryption of a tampered message will throw an `AuthenticationTagMismatchException`, which is caught and re-thrown as a custom exception. Any code calling `Decrypt` must handle this.
*   **Stateful Sessions**: `DoubleRatchetSession` is highly stateful. Do not reuse session objects for different conversations.
*   **Asymmetric Initialization and Roles**: The session's behavior depends heavily on its `SessionRole`.
    *   A `Responder` must be created first. It cannot send a message until it has first received one, which initializes its sending chain.
    *   An `Initiator` requires the `Responder`'s initial public ratchet key (`RatchetPublicKey`) for its constructor. This key must be transmitted from the responder to the initiator.
*   **Ratchet Steps are Asymmetric**: The Diffie-Hellman ratchet step, which provides healing, occurs only within the `Decrypt` method when a new ephemeral key is received. `Encrypt` only performs a symmetric ratchet step. This means the session state changes more significantly on decryption than on encryption.
*   **Key Ownership**: The caller owns and manages the lifecycle of the long-term `identityKey`. The `DoubleRatchetSession` only manages its own internal, ephemeral ratchet keys. Always use a `using` block or manually call `Dispose()` to prevent key leakage.
*   **Serializable Public Keys**: Public keys are exposed as `byte[]` (specifically, in `SubjectPublicKeyInfo` format). This is intentional to ensure they are easily serializable for transport over a network or storage, avoiding platform-specific type dependencies.

## Assumptions

The design and implementation of this library operate on the following assumptions:

1.  **Secure Pre-key Server**: The library assumes the existence of a trusted entity (e.g., a server or a DHT) that can securely store and distribute users' public pre-key bundles. The library is not responsible for the transport of these bundles.
2.  **Domain Purity**: This project will remain a pure domain library. It will not contain any references to UI frameworks, databases, network sockets, or other infrastructure-level concerns. Its dependencies will be minimal.
3.  **Reliable Primitives**: We trust that the underlying cryptographic primitives provided by the .NET Base Class Library (BCL) are implemented correctly and are secure against known attacks. We are not implementing our own primitives.
4.  **Out-of-Band Identity Verification**: This library does not handle the process of verifying a user's identity out-of-band (e.g., by comparing safety numbers or scanning QR codes). It assumes that the identity keys retrieved for a user are authentic.
