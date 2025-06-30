# Percolator.Cryptography

This project is a core domain library for the Percolator chat application. It is responsible for all cryptographic operations required to establish and maintain secure, end-to-end encrypted communication sessions.

## Goal

The primary goal of this library is to provide a self-contained, secure, and well-tested implementation of the cryptographic protocols needed for Percolator. It encapsulates the complexity of modern cryptographic systems, offering a simple API to the application layer for encrypting and decrypting messages.

This library is designed with Domain-Driven Design (DDD) principles in mind. It contains only pure cryptographic logic and is completely isolated from any infrastructure concerns like networking, databases, or user interfaces.

## Persistence Ignorance

A key architectural principle of this domain is that it is **persistence-ignorant**. It contains the logic for stateful protocols like the Double Ratchet, but it does not manage the storage of that state itself.

Instead, it defines persistence interfaces (e.g., `IDoubleRatchetStore`, `IGroupStateStore`) that outline the data that needs to be saved. The `Percolator.Application` project is responsible for implementing these interfaces, allowing it to choose the appropriate storage mechanism (e.g., a database, local files) without affecting the cryptographic logic.

This separation ensures that the `Cryptography` domain remains a pure, testable, and reusable engine focused exclusively on its core security responsibilities.

## AI Assistant Guidance

When modifying this project, adhere to the following architectural rules:

1.  **Domain Independence**: This is a foundational domain library. It **must not** contain references to other domain libraries (e.g., `Percolator.Identity`, `Percolator.Network`). It should have no dependencies on other Percolator projects except for `Percolator.Contracts` if necessary.
2.  **Provide Primitives**: This library's role is to provide low-level, reusable cryptographic primitives and operations (e.g., `CertificateGenerator`). It should not contain business logic specific to other domains.
3.  **Consumed via Interfaces**: Higher-level domains that consume these operations should do so via interfaces defined in their own projects. The `Percolator.Application` layer is responsible for implementing those interfaces and calling the primitives in this library.
4.  **Fail Forward**: Do not add logging for security-sensitive errors. The established pattern is to throw an exception (e.g., `CryptographicException`) to ensure failures are handled by the consuming layer.
5.  **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

## High-Level Concepts

The security of the chat application is built upon several key cryptographic concepts implemented in this library:

-   **One-to-One Messaging (Double Ratchet)**: At the core of our session management is the Signal Double Ratchet algorithm. This provides exceptional security properties for two-party conversations, including:
    -   **Forward Secrecy**: If a user's long-term keys are compromised, past messages cannot be decrypted.
    -   **Future Secrecy (or Post-Compromise Security)**: If a user's keys are compromised, the session can "heal" itself, and future messages will once again be secure after a few message exchanges.

-   **Group Messaging (Sender Keys)**: To support secure and efficient group chats, the library will implement a "sender keys" or multicast encryption protocol. Each member of a group will use the pairwise Double Ratchet channel to securely receive a shared group key, which is then used to encrypt messages sent to the entire group.

    This domain is responsible for managing the complex, stateful cryptographic sessions for each group member (e.g., using a `SenderKeySession`). These sessions are mapped by a stable `Guid`. The `Percolator.Sessions` domain uses this same `Guid` to identify a `GroupConversation`, which is a simple, stateless container for the group's messages. This separation of concerns allows the `Cryptography` domain to focus purely on security, while the `Sessions` domain handles the conversation lifecycle.

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
*   `SenderKeySession`: Manages the state for a secure group conversation from the perspective of a single member.

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

## SenderKeySession

`SenderKeySession` implements the core cryptographic logic for the Sender Keys protocol, enabling secure group messaging. Each member of a group maintains their own `SenderKeySession` instance, initialized with a shared group key.

### Security Properties

-   **Confidentiality**: Messages are encrypted using AES-256-GCM.
-   **Integrity and Authenticity**: Each message is signed with ECDSA P-256, verifying the sender's identity and protecting against tampering.
-   **Forward Secrecy**: The session uses a symmetric-key ratchet. If a member's session key is compromised, an attacker cannot decrypt previous messages sent to the group.
-   **Out-of-Order Message Handling**: The session can handle and decrypt messages that arrive out of sequence, up to a configurable limit.

### Usage Pattern

A trusted group creator generates a random 32-byte session key and securely distributes it to all group members (e.g., over an existing Double Ratchet channel).

```csharp
// 1. All members initialize their session with the same shared key.
var sharedGroupKey = RandomNumberGenerator.GetBytes(32);

using var aliceSession = new SenderKeySession(sharedGroupKey);
using var bobSession = new SenderKeySession(sharedGroupKey);

// 2. Alice sends a message to the group.
var plaintext = Encoding.UTF8.GetBytes("Hello, group!");
var messageFromAlice = aliceSession.Encrypt(plaintext);

// 3. Bob receives and decrypts the message.
// In a real application, Bob would receive messageFromAlice over the network.
var decryptedPlaintext = bobSession.Decrypt(messageFromAlice);

Console.WriteLine(Encoding.UTF8.GetString(decryptedPlaintext)); // "Hello, group!"
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

## Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., invalid cryptographic signatures, malformed packets). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.
