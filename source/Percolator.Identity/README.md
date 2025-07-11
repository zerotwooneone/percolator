# Percolator.Identity

## Core Philosophy: The "Who"

This domain's single responsibility is to answer the question: **"Who is this peer?"**

It manages a peer's core identity, which is defined by a simple, stable `PeerId`. It is fundamentally agnostic of any communication or cryptographic protocol. Its concern is pure, abstract identity. The `Peer` object in this domain contains only `PeerId` and a user-friendly `Name`.

---

This project is a domain library responsible for managing node identities within the Percolator network. It handles the creation, secure storage, and retrieval of cryptographic identities based on a modern multi-key model.

## Core Responsibilities

-   **Manages Host Identities**: Creates, stores, and retrieves the host's cryptographic identity, which is composed of several distinct keys used for signing and key agreement.
-   **Secure Key Storage**: Encrypts all sensitive key material at rest using platform-native data protection APIs (Windows DPAPI).
-   **Source of Truth for Keys**: Acts as the authoritative source for a user's long-lived cryptographic keys.
-   **Provides Key Material**: Exposes interfaces that allow the `Application` layer to retrieve the necessary key material for other domains, such as the public key bundle required by the `Cryptography` domain to perform an X3DH handshake.

## Complex Topics & Security Deep Dive

This section covers the most complex and security-critical aspects of the identity system. A thorough understanding of these topics is essential for any developer working on this part of the codebase.

### 1. The Four-Key Identity Model

Percolator has moved away from a monolithic certificate-based identity to a more flexible and secure multi-key model. Each identity managed by `PersistentKeyManagementService` consists of four distinct keys, each with a single, dedicated purpose:

1.  **Identity Signing Key** (`ECDsa`): A long-term key used exclusively for signing data, such as pre-keys, to prove ownership and prevent tampering. This key establishes the root of trust for an identity.
2.  **Identity Agreement Key** (`ECDiffieHellman`): A long-term key used exclusively for performing Elliptic Curve Diffie-Hellman (ECDH) key agreements during the X3DH handshake.
3.  **Signed Pre-Key** (`ECDiffieHellman`): A medium-term key that is signed by the Identity Signing Key. It is used as part of the X3DH protocol.
4.  **One-Time Pre-Key** (`ECDiffieHellman`): A single-use key used in the X3DH handshake to help provide forward secrecy.

**Security Rationale**: Separating the signing and agreement keys is a critical security principle. If a single key were used for both, a vulnerability in the key agreement protocol could potentially be exploited to forge signatures, leading to a catastrophic failure of the identity system. This four-key model ensures that a compromise in one area does not spill over into another.

### 2. Secure Key Persistence

Storing cryptographic keys securely is paramount. This library uses a two-layer approach to protect keys at rest, orchestrated by `PersistentKeyManagementService` and `CredentialService`.

-   **Layer 1: In-Memory Protection**: The `CredentialService` generates a random, high-entropy password when an identity is first created. This password is held in memory only as long as necessary and is used to password-protect the serialized key data.
-   **Layer 2: Platform-Native Encryption**: The password itself is then encrypted using the Windows Data Protection API (`ProtectedData.Protect`). This binds the encryption to the current user account (and optionally the machine), meaning other users on the same machine cannot access the key material.

This process ensures that even if an attacker gains access to the raw, stored files, they cannot decrypt the keys without also compromising the user's Windows account.

```csharp
// Simplified example of the key persistence flow

// 1. In PersistentKeyManagementService, a new identity's keys are generated.
var x3dhKeys = new X3dhKeys(
    identitySigningKey: signingKey.ExportParameters(true),
    identityAgreementKey: agreementKey.ExportParameters(true),
    signedPreKey: signedPreKey.ExportParameters(true),
    oneTimePreKey: oneTimePreKey.ExportParameters(true)
);

// 2. The keys are serialized to a JSON string.
// WARNING: This JSON contains private key material. See next section.
var serializedKeys = JsonSerializer.Serialize(x3dhKeys, _jsonSerializerOptions);

// 3. The JSON string is encrypted by CredentialService, which uses DPAPI.
// The service generates a password, uses it to encrypt the data, and then
// encrypts the password itself with DPAPI.
var protectedData = await _credentialService.ProtectAsync(Encoding.UTF8.GetBytes(serializedKeys));

// 4. The resulting encrypted blob is written to disk.
await File.WriteAllBytesAsync(filePath, protectedData);
```

### 3. DANGER: Private Key Serialization (`ECParametersJsonConverter`)

**This is the most important security consideration in this project.**

The `ECParametersJsonConverter` is designed to serialize the full `ECParameters` object, which **includes the private key component (`D`)**. This is necessary to persist the keys for local use, allowing the application to be restarted without losing its identity.

However, this presents a significant security risk if the converter is misused. **You must NEVER use this converter or the `JsonSerializerOptions` that contain it to serialize keys for any other purpose**, such as:

-   Logging
-   Sending over a network
-   Displaying in a UI

Exposing the serialized JSON string would leak the private key in plaintext, completely compromising the identity.

**Example of the dangerous serialized output:**

```json
{
  "IdentitySigningKey": {
    "Curve": "nistP256",
    "D": "PRIVATE_KEY_BYTES_HERE", // <-- PRIVATE KEY LEAK
    "Q": {
      "X": "...",
      "Y": "..."
    }
  },
  // ... other keys
}
```

All code handling key serialization must be treated as highly sensitive and subject to strict code review. The only acceptable use case is within the `PersistentKeyManagementService` for writing to and reading from the encrypted local store.

---

## Peer Identification and `PeerId`

In Percolator's peer-to-peer model, identity is handled with a simple and secure approach that cleanly separates the stable, abstract identity from the cryptographic credentials used to secure sessions.

- **`PeerId` is the Authoritative Identifier**: A `PeerId` is a non-cryptographic identifier (e.g., a GUID) used to uniquely and persistently reference a peer. It is the single source of truth for a peer's identity and acts as the foreign key that links data across all other domains (e.g., linking a network address in the Network domain to a public key in the Cryptography domain).

- **Cryptographic Keys are Credentials, Not Identity**: A peer's cryptographic keys (e.g., the `identity_agreement_key`) are treated as powerful but replaceable credentials. They are used by the `Application` layer to look up a peer's `PeerId` upon first contact, but the `PeerId` remains the stable identifier throughout the peer's lifetime.

This clear separation ensures that local application logic (managing a contact list via `PeerId`) is decoupled from the security-critical operations of session establishment, which are based on cryptographic credentials.
