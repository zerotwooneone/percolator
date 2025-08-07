# Percolator.Cryptography

This project is a core domain library for the Percolator chat application. It is responsible for all cryptographic operations required to establish and maintain secure, end-to-end encrypted communication sessions.

## Goal

The primary goal of this library is to provide a self-contained, secure, and well-tested implementation of the cryptographic protocols needed for Percolator. It encapsulates the complexity of modern cryptographic systems, offering a simple API to the application layer for encrypting and decrypting messages.

This library is designed with Domain-Driven Design (DDD) principles in mind. It contains only pure cryptographic logic and is completely isolated from any infrastructure concerns like networking, databases, or user interfaces.

## Design Principles

*   **Domain-Driven Design**: The library is self-contained and exposes its capabilities through a clear, explicit public API. It has no dependencies on other domains.
*   **No Raw `byte[]` in Public APIs**: As a rule, public method signatures in this library do not accept or return raw `byte[]` arrays. Instead, all cryptographic primitives like keys and signatures are wrapped in strongly-typed DDD value objects (e.g., `PublicKey`, `Signature`). This improves type safety and makes the domain language explicit. Data Transfer Objects (DTOs) like `PreKeyBundle` may still contain raw `byte[]` properties for efficient serialization, but they are consumed and produced by methods that adhere to the value-type rule.
*   **Fail Forward**: The library does not handle or log errors. It throws exceptions (e.g., `CryptographicException`) on invalid input or failed cryptographic checks, expecting the application layer to perform necessary validation beforehand.
*   **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`).
*   **Test-Driven Development (TDD)**: All new features and refactoring should follow the Red-Green-Refactor cycle. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

## Key Components

*   `X3DHManager`: Implements the X3DH handshake to establish an initial shared secret.
*   `DoubleRatchetSession`: Manages the ongoing stateful session, handling encryption and decryption of messages.
*   `PreKeyBundle`: A data structure representing a user's public keys needed for the X3DH handshake.
*   `SessionRatchetMessage`: A protobuf-based data structure for transporting the ciphertext and the sender's ephemeral public key.
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

## Library Usage and Security Considerations

The following examples are for developers who wish to use the `Percolator.Cryptography` and related libraries directly in their own applications. Note that some of this functionality, such as group messaging, is not yet exposed in the `Percolator.Node` command-line tool.

### 1. Secure End-to-End Session Establishment and Group Messaging

The following example demonstrates the complete, secure flow for establishing a one-to-one session using the X3DH handshake and then using that secure channel to create a group.

```csharp
using System.Security.Cryptography;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

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
var welcomeMessage = aliceGroupManager.GroupSession.Encrypt(Encoding.UTF8.GetBytes("Welcome!"));
var bobPlaintext = bobGroupManager.GroupSession.Decrypt(welcomeMessage);
var welcomeText = Encoding.UTF8.GetString(bobPlaintext);

// --- 10. CRITICAL: Removing a member ---
// Alice removes a member, which generates re-keying messages for remaining members.
var rekeyMessages = aliceGroupManager.RemoveMember("carol"); // Assuming Carol was added earlier

// Bob processes the re-key message to update his group session state.
bobGroupManager.ProcessRekeyMessage(bobToAlice, rekeyMessages["bob"]);

// Alice sends a new message. Bob can decrypt it, but Carol cannot.
var messageAfterRemoval = aliceGroupManager.GroupSession.Encrypt(Encoding.UTF8.GetBytes("Carol is gone."));
var bobDecryptedAfter = bobGroupManager.GroupSession.Decrypt(messageAfterRemoval);

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

// The library provides built-in authenticated encryption for state persistence.
var masterKey = RandomNumberGenerator.GetBytes(32); // Example key
// Encrypt the state before storing
var encryptedSessionState = CryptoUtils.EncryptAtRest(masterKey, JsonSerializer.SerializeToUtf8Bytes(aliceState), Encoding.UTF8.GetBytes("SessionState"));

// TODO: Store encryptedSessionState securely.

// Later, she can restore it.
// Decrypt the stored state
var decryptedState = CryptoUtils.DecryptAtRest(masterKey, encryptedSessionState, Encoding.UTF8.GetBytes("SessionState"));
var loadedAliceState = JsonSerializer.Deserialize<DoubleRatchetSession.DoubleRatchetSessionState>(decryptedState)!;
// Note: The long-term identity key is NOT serialized and must be provided again.
var loadedAliceSession = new DoubleRatchetSession(loadedAliceState);

### 3. Secure Diagnostic Logging Control

The cryptography library includes diagnostic logging to aid in debugging and troubleshooting. This logging includes hashed values of sensitive cryptographic material like keys and secrets. While these are only hash values and not the actual keys, for maximum security, this diagnostic logging should be disabled in production environments.

The library provides a built-in mechanism to control this logging through the `CryptographyOptions` class:

```csharp
// Create options with secure defaults (diagnostic logging disabled)
var options = CryptographyOptions.CreateSecureDefault();

// OR create options for development with diagnostic logging enabled
var devOptions = CryptographyOptions.CreateDevelopmentDefault();

// Create session with specified options
var x3dhManager = new X3DHManager();
var session = DoubleRatchetSession.AsInitiator(
    sharedSecret, 
    remoteIdentityPublicKey, 
    remoteRatchetPublicKey,
    logger,
    options); // Pass the secure options
```

#### Security Recommendations

1. **Development vs. Production**: Use `CreateDevelopmentDefault()` only in development or testing environments. Always use `CreateSecureDefault()` or explicitly set `EnableCryptographicMaterialLogging = false` in production.

2. **Log Level Control**: In addition to using `CryptographyOptions`, configure your application's logging framework to use appropriate log levels in different environments. For example:
   - Development: `LogLevel.Debug` or `LogLevel.Trace`
   - Production: `LogLevel.Information` or higher

3. **Audit Logging**: If you need to audit cryptographic operations in production, ensure that your audit logs never contain key material, even in hashed form. Instead, log non-sensitive metadata such as operation timestamps, user IDs, or session identifiers.

This secure-by-default approach helps prevent accidental leakage of sensitive cryptographic state in production environments while still allowing detailed diagnostics during development and testing phases.
