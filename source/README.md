# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Key Projects

*   **`Percolator.Cryptography`**: A high-performance, secure cryptography library providing implementations of advanced protocols for secure messaging.
*   **`Percolator.Identity`**: A domain library responsible for managing peer identities, including their cryptographic keys and network endpoint information.
*   **`Percolator.Sessions`**: A domain library that manages the lifecycle of communication sessions and the sequencing of opaque, encrypted messages.
*   **`Percolator.Application`**: The application layer that orchestrates the domain libraries, implementing the system's use cases and business logic.
*   **`Percolator.CryptographyTests`**: A comprehensive test suite for the cryptography library, ensuring its correctness and security through rigorous unit testing.
*   **`Percolator.Node`**: The main executable and command-line interface for the application.

## Overview

The primary component of this solution is the `Percolator.Cryptography` library, which implements a full end-to-end secure messaging system inspired by the Signal Protocol. This includes the X3DH key agreement protocol and the Double Ratchet algorithm for pairwise sessions, as well as a secure group messaging protocol.

## Guidance for AI Assistants

*   **Project Goal**: The main objective of this solution is to provide a robust, secure, and well-tested implementation of modern cryptographic protocols.
*   **Key Components**: The core logic is in `Percolator.Cryptography`. All changes to this library must be accompanied by corresponding tests in `Percolator.CryptographyTests`.
*   **Development Philosophy**: Follow a test-driven development (TDD) approach. Ensure all cryptographic operations use standard, modern, and secure primitives from `.NET`'s `System.Security.Cryptography` namespace. Avoid implementing cryptographic primitives from scratch.
*   **Dependencies**: The project targets a modern .NET version. Ensure cross-platform compatibility.
*   **CLI Implementation Lessons Learned**: The `Percolator.Node` project provides the command-line interface. Several key architectural decisions were made after encountering issues with pre-release packages and dependency injection.
    *   **Avoid Pre-Release Hosting Packages**: The `System.CommandLine.Hosting` package, in its pre-release state, introduced significant breaking changes and instability. The decision was made to revert to a stable version of the core library (`System.CommandLine` v2.0.0-beta4) and remove the hosting dependency entirely.
    *   **Manual Host and DI Management**: Without the hosting package, the application entry point (`Program.cs`) is now responsible for manually configuring and building a dependency injection container. Command handlers use `InvocationContext` to resolve services from this container, and the root command's handler manually configures and launches the Kestrel web server.
    *   **Argument Pre-Parsing for Services**: Services that require configuration from command-line arguments (e.g., `PeerDiscoveryService` needing the `--port`), must be configured *before* the main command parsing begins. This is achieved by performing a lightweight pre-parse of the arguments (`args`) to extract necessary values before building the main service provider.
    *   **`--version` Option Conflict**: The `UseDefaults()` extension method in `System.CommandLine` automatically adds a `--version` option. This can conflict with other mechanisms that also add this option, causing the application to crash on startup. The solution is to avoid `UseDefaults()` and instead register the desired middleware (e.g., `UseHelp()`, `UseExceptionHandler()`) individually.
    *   **Centralized Service Registration**: To maintain a clean composition root in `Percolator.Node`, service registration logic should be centralized in extension methods within the `Percolator.Application` project. These extensions should be grouped by domain (e.g., `AddIdentityServices`, `AddNetworkServices`) to provide a clear and organized way to wire up dependencies.

### Security Best Practices & Lessons Learned

*   **Stateful Group Management**: Securely managing group membership is inherently stateful. The `GroupManager` must track all current members to correctly distribute keys and handle membership changes.
*   **Secure Member Removal (Re-keying)**: When a member is removed from a group, simply deleting them from a list is insufficient. To maintain forward secrecy and prevent the removed member from decrypting future messages, the group **must be re-keyed**. This involves generating a new group session key and securely distributing it to all *remaining* members.
*   **Bounded Caches for Skipped Messages**: Protocols like the Double Ratchet and Sender Key need to handle out-of-order messages by temporarily caching skipped message keys. This cache must have a strict upper bound (e.g., `MaxSkippedMessages`) to prevent a Denial-of-Service (DoS) attack where an attacker forces the client to cache an excessive number of keys, leading to memory exhaustion.
*   **Preventing Cross-Group Attacks**: In a system where a user can be a member of multiple groups, a critical vulnerability can arise if control messages (like a re-key message) are not bound to their specific group context. An attacker could potentially record a re-key message from `Group A` and replay it to a member in `Group B`, causing their session state to become corrupted and out of sync with the rest of the group. To prevent this, all re-key messages contain an `OldGroupId` field, which is verified by the recipient's `GroupManager`. This ensures that the re-key message is only processed if it originated from the group it is intended for, effectively preventing cross-group state confusion attacks.

### gRPC Networking for Peer-to-Peer Communication

When building the peer-to-peer networking layer using gRPC, several critical configuration details were discovered to ensure reliable communication, especially when running multiple nodes on a single machine for testing.

*   **Startup Race Condition**: A race condition can easily occur where the `PeerDiscoveryService` starts broadcasting and attempting to connect to peers *before* the Kestrel gRPC server is fully initialized and ready to accept connections. This leads to persistent "connection refused" errors.
    *   **Solution**: The `IHostedService` adapter pattern can exacerbate this issue. The robust solution is to manage the `PeerDiscoveryService` lifecycle directly. By tying its `StartAsync` and `Stop` methods to the `IHostApplicationLifetime` events (`ApplicationStarted` and `ApplicationStopping`), we guarantee that peer discovery only begins after the gRPC server is confirmed to be listening.

*   **HTTP/2 Protocol Requirement**: gRPC requires the HTTP/2 protocol. When running without TLS (as is common in local development or trusted networks), Kestrel may default to HTTP/1.1, causing connection attempts to fail with an `HTTP_1_1_REQUIRED` error.
    *   **Solution**: Explicitly configure Kestrel to use HTTP/2 on its listening endpoints. This is done by using `ConfigureKestrel` and setting the protocol: `options.ListenAnyIP(port, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });`.

*   **Local Peer Connection IP**: In network environments like Docker or WSL, a node's broadcasted IP address (e.g., `172.25.0.1`) may not be the correct address for another local node to connect to. For inter-process communication on the same machine, the loopback address is the correct and most reliable target.
    *   **Solution**: When a peer is discovered, if it is known to be running on the same machine, the `PeerConnectionManager` should force the gRPC client to connect to `127.0.0.1` (loopback) on the discovered port, rather than using the IP address from the discovery broadcast.

## Security

The Percolator Node is designed with a security-first approach. Key security features include:

-   **End-to-End Encryption**: All gRPC communication between nodes is encrypted using TLS, with identities verified by self-signed X.509 certificates. This prevents eavesdropping and man-in-the-middle attacks.
-   **Cryptographic Peer Discovery**: UDP discovery messages are cryptographically signed to prevent peer spoofing.
-   **Path Traversal Prevention**: The node no longer accepts arbitrary file paths from remote peers. All shared files are managed through a pre-configured, safe directory (`~/PercolatorShares`), eliminating the risk of path traversal and information disclosure attacks.
-   **Denial-of-Service (DoS) Protection**: The application implements service-side rate-limiting to protect against resource exhaustion attacks from malicious peers. It also enforces strict quotas on manifest storage.
-   **Fail-Forward Security Policy**: Domain libraries are designed to throw exceptions on security violations rather than logging warnings, ensuring that insecure states are never ignored.

## Usage

This library is designed to be straightforward to use. Below are examples for common scenarios.

### 1. Establishing a Secure 1-on-1 Session

Before you can communicate, two parties (e.g., Alice and Bob) must establish a `DoubleRatchetSession`. This requires a `sharedSecret` that must be derived from a secure key exchange protocol like X3DH (the implementation of which is outside the scope of this library).

```csharp
// Setup: Alice and Bob both have long-term identity keys.
// Bob has also published an ephemeral ratchet key for this session.
using var aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

// 1. Alice and Bob perform a secure key exchange (e.g., X3DH) to get a shared secret.
// For this example, we'll simulate a simple DH exchange.
var sharedSecret = aliceIdentity.DeriveKeyMaterial(bobIdentity.PublicKey);

// 2. Alice (initiator) creates a session with Bob.
var alicePublicIdentity = aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo();
var bobPublicIdentity = bobIdentity.PublicKey.ExportSubjectPublicKeyInfo();
var bobPublicRatchet = bobRatchetKey.PublicKey.ExportSubjectPublicKeyInfo();

using var aliceToBobSession = DoubleRatchetSession.AsInitiator(
    sharedSecret,
    aliceIdentity,
    bobPublicIdentity,
    bobPublicRatchet
);

// 3. Bob (responder) creates his side of the session.
using var bobToAliceSession = DoubleRatchetSession.AsResponder(
    sharedSecret,
    bobIdentity,
    alicePublicIdentity,
    bobRatchetKey // Bob provides his private ratchet key
);

// Alice can now encrypt a message for Bob.
var message = aliceToBobSession.Encrypt(Encoding.UTF8.GetBytes("Hello, Bob!"));
var plaintextBytes = bobToAliceSession.Decrypt(message);
// plaintext is "Hello, Bob!"
```

### 2. Secure Group Messaging

The `GroupManager` provides a secure way to manage group chats, including the critical ability to remove members and re-key the group.

```csharp
// Prerequisite: Alice, Bob, and Carol have established pairwise DoubleRatchetSessions.
// (aliceToBob, bobToAlice, aliceToCarol, carolToAlice)

// 1. Alice creates a new group, providing her identity key.
var aliceManager = new GroupManager(aliceIdentity);

// 2. Alice invites Bob and Carol to the group.
// First, establish secure 1-on-1 sessions with them.
var sharedSecretBob = aliceIdentity.DeriveKeyMaterial(bobIdentity.PublicKey);
var aliceToBob = DoubleRatchetSession.AsInitiator(sharedSecretBob, aliceIdentity, bobIdentity.PublicKey.ExportSubjectPublicKeyInfo(), bobRatchet.PublicKey.ExportSubjectPublicKeyInfo());

var sharedSecretCarol = aliceIdentity.DeriveKeyMaterial(carolIdentity.PublicKey);
var aliceToCarol = DoubleRatchetSession.AsInitiator(sharedSecretCarol, aliceIdentity, carolIdentity.PublicKey.ExportSubjectPublicKeyInfo(), carolRatchet.PublicKey.ExportSubjectPublicKeyInfo());

var bobInvitation = aliceManager.CreateInvitation("bob", aliceToBob);
var carolInvitation = aliceManager.CreateInvitation("carol", aliceToCarol);
// These invitations are sent to Bob and Carol over their secure 1-on-1 channels.

// 3. Bob and Carol accept their invitations.
// They must be provided with Alice's public signing key and public identity key.
var bobToAlice = DoubleRatchetSession.AsResponder(sharedSecretBob, bobIdentity, aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(), bobRatchet);
var bobManager = GroupManager.AcceptInvitation(bobToAlice, bobInvitation, aliceManager.SigningPublicKey!, aliceIdentity);

var carolToAlice = DoubleRatchetSession.AsResponder(sharedSecretCarol, carolIdentity, aliceIdentity.PublicKey.ExportSubjectPublicKeyInfo(), carolRatchet);
var carolManager = GroupManager.AcceptInvitation(carolToAlice, carolInvitation, aliceManager.SigningPublicKey!, aliceIdentity);

// 4. Alice sends a message to the group.
var welcomeMessage = aliceManager.GroupSession.Encrypt("Welcome!"u8.ToArray());

// Bob and Carol can decrypt it.
// They must receive the message from a trusted source that identifies Alice as the sender.
var bobPlaintext = bobManager.GroupSession.Decrypt(welcomeMessage);
var carolPlaintext = carolManager.GroupSession.Decrypt(welcomeMessage);

// 5. CRITICAL: Alice removes Carol from the group.
var rekeyMessages = aliceManager.RemoveMember("carol");
// A re-key message must now be sent to all remaining members (in this case, just Bob).

// 6. Bob processes the re-key message to update his group session.
bobManager.ProcessRekeyMessage(bobToAlice, rekeyMessages["bob"]);

// 7. Alice sends a new message to the re-keyed group.
var messageAfterRemoval = aliceManager.GroupSession.Encrypt("Carol is gone."u8.ToArray());

// 8. Bob can decrypt the new message, but Carol cannot.
var bobDecryptedAfter = bobManager.GroupSession.Decrypt(messageAfterRemoval);

try
{
    // This will fail with a CryptographicException.
    carolManager.GroupSession.Decrypt(messageAfterRemoval);
}
catch (CryptographicException)
{
    // Carol failed to decrypt the message as expected.
}
```

### Important Security Considerations

### Sender Authentication (Signing Messages)

**Critical:** The `SenderKeySession` and `GroupManager` components are responsible for message *confidentiality* (encryption) in a group setting, but they do **not** provide sender *authentication* (signing). The original implementation included a flawed signing mechanism where any group member could forge messages from any other member. This has been removed.

It is the developer's responsibility to implement sender authentication at a higher protocol layer. The recommended approach is to take the `SenderKeyMessage` object, serialize it, and then sign the serialized data with the sender's unique, long-term identity key (e.g., using `ECDsa.SignData`). The recipient must then verify this signature before passing the `SenderKeyMessage` to the `Decrypt` method.

### State Persistence

Both `DoubleRatchetSession` and `GroupManager` support state serialization so that sessions can be persisted. When persisting this state, you **must** encrypt it at rest using a master key that is securely stored on the device. The library provides `SaveState(masterKey)` and `LoadState(encryptedState, masterKey)` methods for this purpose.

### `OldGroupId` for Re-Key Messages

A `GroupControlMessage` for re-keying now includes an `OldGroupId`. This ensures that a re-key message is cryptographically bound to the specific group it came from, preventing a malicious actor from tricking a user into applying a re-key message from one group to another, which could otherwise lead to state confusion and a denial-of-service attack.

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
