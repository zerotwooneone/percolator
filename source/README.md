# Percolator Solution

This repository contains the Percolator project, a collection of libraries and applications focused on secure, modern software development.

## Key Projects

*   **`Percolator.Cryptography`**: A high-performance, secure cryptography library providing implementations of advanced protocols for secure messaging.
*   **`Percolator.Identity`**: A domain library responsible for managing peer identities, including their cryptographic keys and network endpoint information.
*   **`Percolator.Sessions`**: A domain library that manages the lifecycle of communication sessions and the sequencing of opaque, encrypted messages.
*   **`Percolator.Chat`**: A domain library for chat-specific value types and messaging concepts.
*   **`Percolator.Application`**: The application layer that orchestrates the domain libraries, implementing the system's use cases and business logic.
*   **`Percolator.Infrastructure`**: Infrastructure layer providing persistence, gRPC services, and network transport implementations.
*   **`Percolator.CryptographyTests`**: A comprehensive test suite for the cryptography library, ensuring its correctness and security through rigorous unit testing.
*   **`Percolator.Node`**: The main executable and command-line interface for the application.

## Overview

The primary component of this solution is the `Percolator.Cryptography` library, which implements a full end-to-end secure messaging system inspired by the Signal Protocol. This includes the X3DH key agreement protocol and the Double Ratchet algorithm for pairwise sessions, as well as a secure group messaging protocol.

## Architecture Overview

The solution follows a layered architecture:

- **Domain Layer**: Core domain libraries (`Percolator.Cryptography`, `Percolator.Identity`, `Percolator.Chat`, `Percolator.Sessions`) containing business logic, value types, and cryptographic primitives
- **Application Layer**: `Percolator.Application` orchestrates domain libraries, implements use cases and business logic
- **Infrastructure Layer**: `Percolator.Infrastructure` provides persistence (EF Core), gRPC services, and network transport implementations

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
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.

## AI Development Guidelines

- **No Raw Byte Arrays**: Domain libraries should not send or receive raw byte arrays in or out of the domain. These should be wrapped in DDD value types with clear names so that it is more clear when passing parameters or returning results. This applies to both domain boundaries AND network/application layers. Examples of strongly-typed wrappers include `RatchetIdentityKey`, `Signature`, `DeliveryCertificatePayloadBytes`, and `Ed25519SignatureBytes`.
- **Rule 6 Transport Adherence**: No database identifiers (e.g., PeerId Guid) may be transmitted over the wire. Only cryptographic public key fingerprints (PKH) are allowed in all network payloads to prevent database identifier leakage.
- **Strongly-Typed IDs**: To enhance type safety and clarify intent, raw `Guid` primitives must not be used for identifiers in public APIs. Instead, wrap them in strongly-typed DDD value objects with intention-revealing names (e.g., `PeerId`, `ConversationId`). This prevents accidental misuse of identifiers and makes the domain language more explicit.
- **Test-Driven Development**: All new features and refactoring should follow the Red-Green-Refactor cycle of Test-Driven Development (TDD). Write a failing test first (Red), then write the simplest code to make it pass (Green), and finally, refactor the code to improve its design while keeping the tests passing. This ensures that all logic is covered by tests and promotes a high-quality, maintainable codebase.

## Security

The Percolator Node is designed with a security-first approach. Key security features include:

-   **End-to-End Encryption**: Communication is protected by a multi-layered encryption scheme. The underlying gRPC transport can be secured with TLS. On top of that, all peer-to-peer sessions use the Double Ratchet protocol, providing forward secrecy and post-compromise security for all messages.
-   **Cryptographic Identities**: Peer identities are based on a set of public cryptographic keys (following the X3DH protocol), not on certificates. All key exchange operations are signed to prevent impersonation and man-in-the-middle attacks.
-   **Cryptographic Peer Discovery**: UDP discovery messages are cryptographically signed to prevent peer spoofing.
-   **Path Traversal Prevention**: The node no longer accepts arbitrary file paths from remote peers. All shared files are managed through a pre-configured, safe directory (`~/PercolatorShares`), eliminating the risk of path traversal and information disclosure attacks.
-   **Denial-of-Service (DoS) Protection**: The application implements service-side rate-limiting to protect against resource exhaustion attacks from malicious peers. It also enforces strict quotas on manifest storage and has bounded caches for out-of-order messages.
-   **Fail-Forward Security Policy**: Domain libraries are designed to throw exceptions on security violations rather than logging warnings, ensuring that insecure states are never ignored.

## Security Model: Mutual TLS and Trust On First Use (TOFU)

The peer-to-peer communication in Percolator is secured by a strict mutual TLS (mTLS) handshake protocol combined with a Trust On First Use (TOFU) model for peer validation. Both the client (initiator) and server (responder) must present valid, self-signed certificates to establish a connection.

### Key Requirements

1.  **Custom Certificate Extension**: All TLS certificates used for peer communication **must** contain a custom X.509 extension with the OID `1.3.6.1.4.1.58753.1.1` (`PeerIdentityKey`). The value of this extension must be the ASN.1 DER-encoded public key of the peer's identity signing key (an `OCTET STRING`). This extension is the primary mechanism for identifying a certificate as a valid Percolator peer certificate.

2.  **Client-Side Handshake**: When initiating a connection, the client (`GrpcClientFactory`) must:
    *   Attach its own self-signed TLS certificate (containing the custom OID) to the `HttpClientHandler`.
    *   Implement a custom server certificate validation callback (`ServerCertificateCustomValidationCallback`). This callback implements the TOFU logic:
        *   If the peer is known, the presented server certificate **must** match the certificate stored for that peer.
        *   If the peer is unknown, the client trusts the presented certificate on first use and stores it for future validation.

3.  **Server-Side Handshake**: When accepting a connection, the Kestrel server must:
    *   Be configured to `RequireCertificate` for all incoming TLS connections.
    *   Implement a custom client certificate validation callback (`ClientCertificateValidation`). This callback **must**:
        *   Verify that the incoming client certificate contains the custom `PeerIdentityKey` OID extension.
        *   Decode the ASN.1 `OCTET STRING` from the extension's raw data to ensure it contains a valid public key.

This end-to-end configuration ensures that only authenticated and authorized Percolator nodes can communicate with each other, preventing unauthorized access and man-in-the-middle attacks.

### Complex Topics: Secure Session Establishment

The security of all peer-to-peer communication in Percolator relies on a two-phase process for establishing and maintaining secure sessions. This process is orchestrated by the `DirectSessionManager` and backed by the cryptographic primitives in `Percolator.Cryptography`.

#### Phase 1: The X3DH Handshake

When one peer (the initiator, e.g., Bob) wants to communicate with another (the responder, e.g., Alice) for the first time, they perform the **Extended Triple Diffie-Hellman (X3DH)** handshake.

1.  **Alice Publishes Keys**: Alice generates a set of long-term and medium-term cryptographic keys and publishes them as a "pre-key bundle." This bundle contains:
    *   Her long-term public identity key (`IK_A`).
    *   A signed pre-key (`SPK_A`).
    *   A batch of one-time pre-keys (`OPK_A`).

2.  **Bob Fetches Bundle and Initiates**: Bob fetches one of Alice's pre-key bundles. He then:
    *   Generates his own ephemeral key pair (`EK_B`).
    *   Performs three Diffie-Hellman key agreements:
        1.  `DH1 = DH(IK_B, SPK_A)`
        2.  `DH2 = DH(EK_B, IK_A)`
        3.  `DH3 = DH(EK_B, SPK_A)`
    *   If a one-time pre-key is available, he performs a fourth: `DH4 = DH(EK_B, OPK_A)`.

3.  **Shared Secret Derivation**: Bob combines the results of these Diffie-Hellman agreements and feeds them into a Key Derivation Function (KDF) to produce a single, strong `SharedSecret`.

This `SharedSecret` is the initial root key for the Double Ratchet session. Bob can now use it to encrypt his first message to Alice.

#### Phase 2: The Double Ratchet Protocol

Once the initial shared secret is established, all subsequent communication is protected by the **Double Ratchet** algorithm. This algorithm ensures that every message is encrypted with a unique, ephemeral key, providing exceptional security guarantees.

The "Double" Ratchet has two components:

1.  **The Symmetric-Key Ratchet**: After each message is sent or received, a KDF is used to derive a new message key from the previous one. This is like a ratchet that clicks forward with every message, ensuring that a compromised message key cannot be used to decrypt past or future messages in the same chain.

2.  **The Diffie-Hellman Ratchet**: Whenever the conversation flows from one party to the other, a new Diffie-Hellman key agreement is performed using new ephemeral keys. The result of this handshake is used to re-seed the symmetric-key ratchet. This provides **post-compromise security**; if an attacker steals a party's keys, the session can "heal" itself as soon as the legitimate parties exchange a new DH-ratcheted message.

This two-phase process, orchestrated by `DirectSessionManager`, provides end-to-end encryption with forward secrecy and post-compromise security, making peer-to-peer communication extremely secure.

## Usage

The Percolator Node is a command-line application for secure peer-to-peer communication.

### 1. Host a Node

To start a node and listen for incoming connections, use the `host` command. This will start a gRPC server on a configured port (default is 5001) and begin broadcasting its presence on the local network.

In one terminal (this will be **Alice**):
```bash
dotnet run --project .\Percolator.Node\ -- host
```
This will generate a new identity for Alice if one doesn't exist and save it securely on disk.

### 2. Connect to a Peer

In a second terminal (this will be **Bob**), start another node:
```bash
dotnet run --project .\Percolator.Node\ -- host
```

Now, from Bob's terminal, connect to Alice. Assuming Alice's node is running on the same machine at `localhost:5001`:
```bash
dotnet run --project .\Percolator.Node\ -- connect localhost 5001
```
This command will perform a secure key exchange (X3DH) with Alice's node and establish a secure session. A unique `Conversation ID` will be printed to the console. You will need this ID to send messages.

Example output:
```
info: Percolator.Node.Program[0]
      Connecting to localhost:5001...
info: Percolator.Node.Program[0]
      Session established with peer. Conversation ID: 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d
```

### 3. Send a Secure Message

Once a session is established, you can send encrypted messages using the `send` command. Use the `Conversation ID` from the previous step.

From Bob's terminal:
```bash
dotnet run --project .\Percolator.Node\ -- send 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello, Alice!"
```
The message will be encrypted and sent to Alice. Alice's node, which is running the `host` command, will automatically receive, decrypt, and display the message.

Alice's terminal will show:
```
info: Percolator.Application.Messages.MessageReceivedHandler[0]
      Received message for conversation 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d: Hello, Alice!

```
