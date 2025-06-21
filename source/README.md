# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Key Projects

*   **`Pecolator.Cryptography`**: A high-performance, secure cryptography library providing implementations of advanced protocols for secure messaging.
*   **`Percolator.CryptographyTests`**: A comprehensive test suite for the cryptography library, ensuring its correctness and security through rigorous unit testing.

## Overview

The primary component of this solution is the `Pecolator.Cryptography` library, which implements a full end-to-end secure messaging system based on the Signal Protocol. This includes the X3DH key agreement protocol and the Double Ratchet algorithm.

## Guidance for AI Assistants

*   **Project Goal**: The main objective of this solution is to provide a robust, secure, and well-tested implementation of modern cryptographic protocols.
*   **Key Components**: The core logic is in `Pecolator.Cryptography`. All changes to this library must be accompanied by corresponding tests in `Percolator.CryptographyTests`.
*   **Development Philosophy**: Follow a test-driven development (TDD) approach. Ensure all cryptographic operations use standard, modern, and secure primitives from `.NET`'s `System.Security.Cryptography` namespace. Avoid implementing cryptographic primitives from scratch.
*   **Dependencies**: The project targets a modern .NET version. Ensure cross-platform compatibility.

## Usage

This library is designed to be straightforward to use. Below are examples for common scenarios.

### 1. Establishing a Secure 1-on-1 Session

Before you can communicate, two parties (e.g., Alice and Bob) must establish a `DoubleRatchetSession`. This is done using a simplified X3DH-like key agreement flow.

```csharp
// Setup: Alice and Bob both have long-term identity keys.
// Bob has also published an ephemeral pre-key.
using var aliceIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobIdentityKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
using var bobPreKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

// Alice initiates the session using Bob's public keys.
var sharedSecret = aliceIdentityKey.DeriveKeyMaterial(bobPreKey.PublicKey);
var aliceSession = new DoubleRatchetSession(
    sharedSecret,
    aliceIdentityKey,
    SessionRole.Initiator,
    bobPreKey.PublicKey.ExportSubjectPublicKeyInfo()
);

// Bob establishes his side of the session using his keys.
var bobSession = new DoubleRatchetSession(
    sharedSecret,
    bobIdentityKey,
    SessionRole.Responder,
    ownInitialRatchetKey: bobPreKey
);

// Alice can now encrypt a message for Bob.
var message = aliceSession.Encrypt(Encoding.UTF8.GetBytes("Hello, Bob!"));
var plaintext = bobSession.Decrypt(message);
// plaintext is "Hello, Bob!"
```

### 2. Group Messaging with `GroupManager`

Once a secure 1-on-1 session is established, it can be used to securely invite a member to a new group.

```csharp
// Prerequisite: Alice and Bob have an established DoubleRatchetSession.
// (aliceToBobSession and bobToAliceSession from the example above)

// 1. Alice creates a new group and an invitation for Bob.
var aliceManager = new GroupManager();
var (invitation, aliceGroupSession) = aliceManager.CreateGroupAndInvitation(aliceSession);

// The 'invitation' is a standard RatchetMessage that can be sent over the 1-on-1 channel.

// 2. Bob receives the invitation and accepts it.
var bobManager = new GroupManager();
var bobGroupSession = bobManager.AcceptInvitation(bobSession, invitation);

// 3. Alice and Bob can now communicate in the group.
var groupMessage = aliceGroupSession.Encrypt(Encoding.UTF8.GetBytes("Welcome to the group!"));
var decryptedGroupMessage = bobGroupSession.Decrypt(groupMessage);
// decryptedGroupMessage is "Welcome to the group!"
```

### 3. Session Persistence

To support long-running, asynchronous conversations, both `DoubleRatchetSession` and `SenderKeySession` can be serialized to a byte array and restored later.

```csharp
// --- DoubleRatchetSession Persistence ---

// Alice saves her session state.
var aliceStateBytes = aliceSession.SaveState();

// Later, she can restore it.
// Note: The long-term identity key is NOT serialized and must be provided again.
var loadedAliceSession = DoubleRatchetSession.LoadState(aliceStateBytes, aliceIdentityKey);


// --- SenderKeySession Persistence ---

// Alice saves her group session state.
var groupStateBytes = aliceGroupSession.SaveState();

// Later, she can restore it.
var loadedGroupSession = SenderKeySession.LoadState(groupStateBytes);
