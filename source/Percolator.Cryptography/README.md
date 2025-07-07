# Percolator.Cryptography

This project is a core domain library for the Percolator chat application. It is responsible for all cryptographic operations required to establish and maintain secure, end-to-end encrypted communication sessions.

## Goal

The primary goal of this library is to provide a self-contained, secure, and well-tested implementation of the cryptographic protocols needed for Percolator. It encapsulates the complexity of modern cryptographic systems, offering a simple API to the application layer for encrypting and decrypting messages.

This library is designed with Domain-Driven Design (DDD) principles in mind. It contains only pure cryptographic logic and is completely isolated from any infrastructure concerns like networking, databases, or user interfaces.

## Design Principles

*   **Domain-Driven Design**: The library is self-contained and exposes its capabilities through a clear, explicit public API. It has no dependencies on other domains.
*   **No Raw `byte[]` in Public APIs**: As a rule, public method signatures in this library do not accept or return raw `byte[]` arrays. Instead, all cryptographic primitives like keys and signatures are wrapped in strongly-typed DDD value objects (e.g., `PublicKey`, `Signature`). This improves type safety and makes the domain language explicit. Data Transfer Objects (DTOs) like `PreKeyBundle` may still contain raw `byte[]` properties for efficient serialization, but they are consumed and produced by methods that adhere to the value-type rule.
*   **Fail Forward**: The library does not handle or log errors. It throws exceptions (e.g., `CryptographicException`) on invalid input or failed cryptographic checks, expecting the application layer to perform necessary validation beforehand.

## Key Components

*   `X3DHManager`: Implements the X3DH handshake to establish an initial shared secret.
*   `DoubleRatchetSession`: Manages the ongoing stateful session, handling encryption and decryption of messages.
*   `PreKeyBundle`: A data structure representing a user's public keys needed for the X3DH handshake.
*   `RatchetMessage`: A data structure for transporting the ciphertext and the sender's ephemeral public key.
*   `SenderKeySession`: Manages the state for a secure group conversation from the perspective of a single member.

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

## Library Usage and Security Considerations

The following examples are for developers who wish to use the `Percolator.Cryptography` and related libraries directly in their own applications. Note that some of this functionality, such as group messaging, is not yet exposed in the `Percolator.Node` command-line tool.

### 1. Secure End-to-End Session Establishment and Group Messaging

The following example demonstrates the complete, secure flow for establishing a one-to-one session using the X3DH handshake and then using that secure channel to create a group.

```csharp
using System.Security.Cryptography;
using Percolator.Cryptography;

// Helper function to create identity keys
void CreateIdentity(out ECDsa signingKey, out ECDiffieHellman agreementKey)
{
    signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    agreementKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
}

// --- 1. Setup: Alice and Bob create their long-term identity keys ---
CreateIdentity(out var aliceSigningKey, out var aliceAgreementKey);
CreateIdentity(out var bobSigningKey, out var bobAgreementKey);

// --- 2. Bob (the Responder) creates and publishes his PreKeyBundle ---
var x3dhManager = new X3DHManager();
using var bobSignedPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobOneTimePreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

// Bob signs his signed pre-key's public key
var bobSignedPreKeyPublicBytes = bobSignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
var bobSignature = x3dhManager.SignPreKey(bobSigningKey, bobSignedPreKeyPublicBytes);

// Bob creates his bundle for Alice to fetch from a server
var bobBundle = new PreKeyBundle(
    IdentityAgreementKey: bobAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(),
    IdentitySigningKey: bobSigningKey.PublicKey.ExportSubjectPublicKeyInfo(),
    SignedPreKey: bobSignedPreKeyPublicBytes,
    Signature: bobSignature,
    OneTimePreKey: bobOneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo()
);

// --- 3. Alice (the Initiator) initiates the handshake ---
// Alice fetches Bob's PreKeyBundle from the server.
using var aliceEphemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
var sharedSecretAlice = x3dhManager.InitiateHandshake(bobBundle, aliceEphemeralKey, aliceAgreementKey);

// --- 4. Alice establishes a DoubleRatchetSession with Bob ---
var aliceToBob = DoubleRatchetSession.AsInitiator(
    sharedSecretAlice.Value,
    aliceAgreementKey,
    bobBundle.IdentitySigningKey, // Bob's public signing key
    bobBundle.SignedPreKey      // Bob's public signed pre-key
);

// --- 5. Alice creates a group and invites Bob ---
var aliceGroupManager = new GroupManager(aliceAgreementKey);
// The invitation is encrypted using the newly established 1-on-1 session
var invitationToBob = aliceGroupManager.CreateInvitation("bob", aliceToBob);

// Alice sends the invitation to Bob. This is the first application message.

// --- 6. Bob (the Responder) receives the message and completes the handshake ---
// Bob needs Alice's public keys, which would be sent with the initial message.
var aliceIdentityAgreementKeyPublicBytes = aliceAgreementKey.PublicKey.ExportSubjectPublicKeyInfo();
var aliceEphemeralKeyPublicBytes = aliceEphemeralKey.PublicKey.ExportSubjectPublicKeyInfo();

var sharedSecretBob = x3dhManager.RespondToHandshake(
    aliceIdentityAgreementKeyPublicBytes,
    aliceEphemeralKeyPublicBytes,
    bobSigningKey,
    bobAgreementKey,
    bobSignedPreKey,
    bobOneTimePreKey
);

// --- 7. Bob establishes his side of the DoubleRatchetSession ---
var bobToAlice = DoubleRatchetSession.AsResponder(
    sharedSecretBob.Value,
    bobAgreementKey,
    aliceAgreementKey.PublicKey.ExportSubjectPublicKeyInfo(), // Alice's public identity key
    bobSignedPreKey
);

// --- 8. Bob accepts the group invitation ---
// Bob decrypts the invitation and uses the payload to accept.
var bobGroupManager = GroupManager.AcceptInvitation(
    bobToAlice,
    invitationToBob,
    aliceGroupManager.SigningPublicKey!,
    aliceAgreementKey
);

// --- 9. Secure communication is established! ---
var welcomeMessage = aliceGroupManager.GroupSession.Encrypt("Welcome!"u8.ToArray());
var bobPlaintext = bobGroupManager.GroupSession.Decrypt(welcomeMessage);

// --- 10. CRITICAL: Removing a member ---
// Alice removes a member, which generates re-keying messages for remaining members.
var rekeyMessages = aliceGroupManager.RemoveMember("carol"); // Assuming Carol was added earlier

// Bob processes the re-key message to update his group session state.
bobGroupManager.ProcessRekeyMessage(bobToAlice, rekeyMessages["bob"]);

// Alice sends a new message. Bob can decrypt it, but Carol cannot.
var messageAfterRemoval = aliceGroupManager.GroupSession.Encrypt("Carol is gone."u8.ToArray());
var bobDecryptedAfter = bobGroupManager.GroupSession.Decrypt(messageAfterRemoval);
```

### 2. State Persistence

Both `DoubleRatchetSession` and `GroupManager` support state serialization so that sessions can be persisted. When persisting this state, you **must** encrypt it at rest using a master key that is securely stored on the device.

```csharp
using System.Text.Json;
using System.Security.Cryptography;

// --- DoubleRatchetSession Persistence ---
// Alice gets her session state.
var aliceState = aliceToBobSession.GetState();

// She can serialize it to JSON.
var aliceStateJson = JsonSerializer.Serialize(aliceState);

// TODO: Encrypt aliceStateJson before storing it securely.

// Later, she can restore it.
// TODO: Decrypt the state JSON before deserializing.
var loadedAliceState = JsonSerializer.Deserialize<DoubleRatchetSession.DoubleRatchetSessionState>(aliceStateJson)!;
// Note: The long-term identity key is NOT serialized and must be provided again.
var loadedAliceSession = new DoubleRatchetSession(loadedAliceState, aliceIdentity);


// --- GroupManager Persistence ---
// The library provides built-in authenticated encryption for state persistence.
// You must provide a master key, which you should derive using a secure KDF
// like Argon2 or PBKDF2 from a user password or other secret.
var masterKey = RandomNumberGenerator.GetBytes(32); // Example key

// Alice saves her group manager state. The result is encrypted.
var encryptedState = aliceManager.SaveState(masterKey);

// The encrypted state can be stored safely.

// Later, she can restore it using the same master key and her identity key.
var loadedGroupManager = GroupManager.LoadState(encryptedState, masterKey, aliceIdentity);
```

### Important Security Considerations for Library Developers

#### Sender Authentication (Signing Messages)

**Critical:** The `SenderKeySession` and `GroupManager` components are responsible for message *confidentiality* (encryption) in a group setting, but they do **not** provide sender *authentication* (signing). The original implementation included a flawed signing mechanism where any group member could forge messages from any other member. This has been removed.

It is the developer's responsibility to implement sender authentication at a higher protocol layer. The recommended approach is to take the `SenderKeyMessage` object, serialize it, and then sign the serialized data with the sender's unique, long-term identity key (e.g., using `ECDsa.SignData`). The recipient must then verify this signature before passing the `SenderKeyMessage` to the `Decrypt` method.

#### `OldGroupId` for Re-Key Messages

A `GroupControlMessage` for re-keying now includes an `OldGroupId`. This ensures that a re-key message is cryptographically bound to the specific group it came from, preventing a malicious actor from tricking a user into applying a re-key message from one group to another, which could otherwise lead to state confusion and a denial-of-service attack.

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

## Assumptions

The design and implementation of this library operate on the following assumptions:

1.  **Secure Pre-key Server**: The library assumes the existence of a trusted entity (e.g., a server or a DHT) that can securely store and distribute users' public pre-key bundles. The library is not responsible for the transport of these bundles.
2.  **Domain Purity**: This project will remain a pure domain library. It will not contain any references to UI frameworks, databases, network sockets, or other infrastructure-level concerns. Its dependencies will be minimal.
3.  **Reliable Primitives**: We trust that the underlying cryptographic primitives provided by the .NET Base Class Library (BCL) are implemented correctly and are secure against known attacks. We are not implementing our own primitives.
4.  **Out-of-Band Identity Verification**: This library does not handle the process of verifying a user's identity out-of-band (e.g., by comparing safety numbers or scanning QR codes). It assumes that the identity keys retrieved for a user are authentic.

## Error Handling and Security

This domain adheres to a strict "fail forward" security policy. Methods must not log warnings or errors for security-sensitive violations (e.g., invalid cryptographic signatures, malformed packets). Instead, they **must** throw an appropriate exception, typically a `System.Security.SecurityException`.

This ensures that security violations are never ignored and are always propagated up to the consuming layer, preventing the system from continuing in an insecure or indeterminate state. The responsibility for handling these exceptions and preventing them through input validation lies with the `Percolator.Application` layer.
