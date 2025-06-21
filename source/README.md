# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Key Projects

*   **`Percolator.Cryptography`**: A high-performance, secure cryptography library providing implementations of advanced protocols for secure messaging.
*   **`Percolator.CryptographyTests`**: A comprehensive test suite for the cryptography library, ensuring its correctness and security through rigorous unit testing.

## Overview

The primary component of this solution is the `Percolator.Cryptography` library, which implements a full end-to-end secure messaging system inspired by the Signal Protocol. This includes the X3DH key agreement protocol and the Double Ratchet algorithm for pairwise sessions, as well as a secure group messaging protocol.

## Guidance for AI Assistants

*   **Project Goal**: The main objective of this solution is to provide a robust, secure, and well-tested implementation of modern cryptographic protocols.
*   **Key Components**: The core logic is in `Percolator.Cryptography`. All changes to this library must be accompanied by corresponding tests in `Percolator.CryptographyTests`.
*   **Development Philosophy**: Follow a test-driven development (TDD) approach. Ensure all cryptographic operations use standard, modern, and secure primitives from `.NET`'s `System.Security.Cryptography` namespace. Avoid implementing cryptographic primitives from scratch.
*   **Dependencies**: The project targets a modern .NET version. Ensure cross-platform compatibility.

### Security Best Practices & Lessons Learned

*   **Stateful Group Management**: Securely managing group membership is inherently stateful. The `GroupManager` must track all current members to correctly distribute keys and handle membership changes.
*   **Secure Member Removal (Re-keying)**: When a member is removed from a group, simply deleting them from a list is insufficient. To maintain forward secrecy and prevent the removed member from decrypting future messages, the group **must be re-keyed**. This involves generating a new group session key and securely distributing it to all *remaining* members.
*   **Bounded Caches for Skipped Messages**: Protocols like the Double Ratchet and Sender Key need to handle out-of-order messages by temporarily caching skipped message keys. This cache must have a strict upper bound (e.g., `MaxSkippedMessages`) to prevent a Denial-of-Service (DoS) attack where an attacker forces the client to cache an excessive number of keys, leading to memory exhaustion.

## Usage

This library is designed to be straightforward to use. Below are examples for common scenarios.

### 1. Establishing a Secure 1-on-1 Session

Before you can communicate, two parties (e.g., Alice and Bob) must establish a `DoubleRatchetSession`.

```csharp
// Setup: Alice and Bob both have long-term identity keys.
// Bob has also published an ephemeral ratchet key for this session.
using var aliceIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobIdentity = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobRatchetKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

// Alice (initiator) creates a session with Bob.
using var aliceToBobSession = DoubleRatchetSession.CreateInitiatorSession(
    aliceIdentity,
    bobIdentity,
    bobRatchetKey
);

// Bob (responder) creates his side of the session.
using var bobToAliceSession = DoubleRatchetSession.CreateResponderSession(
    bobIdentity,
    bobRatchetKey, // Bob provides his private ratchet key
    aliceIdentity
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
var bobInvitation = aliceManager.CreateInvitation("bob", aliceToBob);
var carolInvitation = aliceManager.CreateInvitation("carol", aliceToCarol);
// These invitations are sent to Bob and Carol over their secure 1-on-1 channels.

// 3. Bob and Carol accept their invitations.
// They must be provided with Alice's public signing key and public identity key.
var bobManager = GroupManager.AcceptInvitation(bobToAlice, bobInvitation, aliceManager.SigningPublicKey!, aliceIdentity);
var carolManager = GroupManager.AcceptInvitation(carolToAlice, carolInvitation, aliceManager.SigningPublicKey!, aliceIdentity);

// 4. Alice sends a message to the group.
var welcomeMessage = aliceManager.GroupSession.Encrypt("Welcome!"u8.ToArray());

// Bob and Carol can decrypt it.
var bobsDecrypted = bobManager.GroupSession.Decrypt(welcomeMessage);
var carolsDecrypted = carolManager.GroupSession.Decrypt(welcomeMessage);

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

### 3. Session Persistence

To support long-running, asynchronous conversations, sessions can be serialized. The library provides a state object that can be serialized to JSON (or any other format).
**IMPORTANT**: For `DoubleRatchetSession`, the user is responsible for encrypting the serialized state at rest. `GroupManager` provides built-in encryption.

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
