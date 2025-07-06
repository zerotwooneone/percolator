# Percolator.Node

This project is the main executable entry point for a peer in the Percolator network.

## Purpose

The `Percolator.Node` application is responsible for bootstrapping and running all the necessary services for a peer to participate in the network. It integrates components from the various class libraries (`Application`, `Network`, etc.) into a runnable host.

## Key Responsibilities:

-   **Hosting**: Sets up and runs the ASP.NET Core host for the gRPC server via the `host` command.
-   **Service Startup**: Initializes and starts all necessary background services for peer communication.
-   **Command-Line Interface**: Provides a clear and simple command-line interface for hosting, connecting to peers, and sending messages.
-   **Secure Sessions**: Establishes end-to-end encrypted communication channels using a modern cryptographic handshake (X3DH).
-   **Identity Management**: Securely generates and manages the user's cryptographic identity.

## Operational Modes

The node operates through distinct commands:

-   **Host Mode**: Run the `host` command to start the node, listen for incoming connections, and host the gRPC service. This makes your node available to other peers.
-   **Connect Mode**: Use the `connect` command to initiate a secure session with a hosting peer.
-   **Send Mode**: Once a session is established, use the `send` command with a valid `conversationId` to send encrypted messages.

## Platform Dependencies

### Windows Only

This application is currently **Windows-only**. This is because it relies on the `Percolator.Application` library, which uses the Windows Data Protection API (DPAPI) for securely storing cryptographic keys.

## Usage

The `Percolator.Node` executable is driven by a simple set of commands for hosting, connecting, and sending messages.

### 1. Start a Host & Get an Invitation Link

To run the application as a network host, use the `host` command. This will start the gRPC server, allow other peers to connect to you, and generate a unique, shareable invitation link.

-   `--port` / `-p`: The port to listen on (default: `5000`).
-   `--identity` / `-i`: The name of the identity to use (default: `default`). A new identity will be created if it doesn't exist.

**Example:**
```bash
dotnet run --project .\Percolator.Node\ -- host --identity Alice
```

The host will start and display an invitation link. **Copy this entire link** to share it with a peer who wants to connect to you.

```
Host started successfully.
Invitation Link: percolator://localhost:5000/MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx...long_public_key...=
Share this link with peers who want to connect.
```

### 2. Connect to a Peer

To establish a secure session with a host, use the `connect` command with the invitation link you received from them.

-   `<invitation-link>`: The full `percolator://` link provided by the host.
-   `--identity` / `-i`: The name of the local identity to use for the connection.

**Example:**
```bash
dotnet run --project .\Percolator.Node\ -- connect "percolator://localhost:5000/MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx..." --identity Bob
```

Upon success, the command will output a unique `Conversation ID`. **Save this ID**, as you will need it to send messages.

### 3. Send a Message

Once a session is established, use the `send` command to send an encrypted message. You must specify the `conversationId` and the identity associated with that conversation.

-   `<conversationId>`: The unique ID generated when you connected to the peer.
-   `<message>`: The plaintext message you want to send, enclosed in quotes.
-   `--identity` / `-i`: The name of the local identity that established the session.

**Example:**
```bash
dotnet run --project .\Percolator.Node\ -- send 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello, world!" --identity Bob
```

## Example Scenario: Two Nodes on One Machine

This scenario demonstrates how to start two independent nodes and have one send a message to the other. You will need two separate terminal windows.

### Terminal 1: Start Node A (Host)

This node will act as the host, listening for connections with the identity `Alice`. It will generate an invitation link for Node B to use.

```bash
dotnet run --project .\Percolator.Node\ -- host --identity Alice
```

After the host starts, **copy the `Invitation Link`** that is displayed in the console.

### Terminal 2: Connect and Send Message with Node B

In a second terminal, use the `Bob` identity to connect to `Alice` using her invitation link.

**Step 1: Connect to Node A**

Paste the full invitation link you copied from Terminal 1.

```bash
dotnet run --project .\Percolator.Node\ -- connect "percolator://localhost:5000/MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx..." --identity Bob
```

This will output a `Conversation ID`. Copy it for the next step.

**Step 2: Send a message**

Replace `<conversation_id>` with the ID from the previous step.

```bash
dotnet run --project .\Percolator.Node\ -- send <conversation_id> "Hello from Bob!" --identity Bob
```

You should see the message appear in the console for Node A.

## Important Note on PeerId

A `PeerId` is a **local-only, non-cryptographic identifier**. It is randomly generated (as a GUID) and is used to uniquely identify a peer within the local application instance.

**Key Principles:**
-   **Local Scope:** A `PeerId` is only meaningful to the local application. It is never shared with remote peers.
-   **Not for Authentication:** It MUST NOT be used for authentication or as a security credential. All security operations (like session management) are tied to cryptographic keys, not the `PeerId`.
-   **Stable Identifier:** It allows the application to maintain a stable reference to a peer, even if that peer's underlying cryptographic keys change.

This rule is enforced across all projects in the solution to ensure a clear and secure identity model.

## Guidance for AI Assistants

*   **`System.CommandLine` Version**: The project is standardized on `System.CommandLine` version `2.0.0-beta4`. Do not upgrade to newer pre-release versions or introduce the `System.CommandLine.Hosting` package, as this led to significant instability and breaking changes.
*   **Manual Dependency Injection**: The application manually configures its own dependency injection container in `Program.cs`. It does not use the .NET Generic Host for command-line integration. Command handlers must resolve their dependencies from the `IServiceProvider` made available via the `InvocationContext`.
*   **Manual Host Lifecycle**: The Kestrel web server is started manually within the root command's handler (`RunNodeAsync`). It is not managed automatically by a hosting library.
*   **Argument Pre-Parsing for Services**: Some services, like `PeerDiscoveryService`, require configuration values (e.g., the listening port) that are provided via command-line arguments. To handle this, `Program.cs` performs a lightweight pre-parse of the `args` to extract these values *before* the main DI container is built. This ensures services are constructed with the correct configuration.
*   **`--version` Option Conflict**: The `UseDefaults()` extension method in `System.CommandLine` was found to cause a runtime crash by implicitly adding a `--version` option that conflicted with another registration. To avoid this, middleware (like `UseHelp()`, `UseExceptionHandler()`, etc.) is added individually to the `CommandLineBuilder`.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.
    *   **Investigate Errors Systematically**: A build error is a clue, not a conclusion. When an error like "type not found" occurs, do not invent the type. Instead, use tools to search the existing codebase for the correct type that fulfills the required role.
    *   **Use Precise, Definition-Oriented Searches**: When searching for a type, search for its definition (e.g., `grep "class MyClass"`), not just its name, to avoid ambiguity.
    *   **Work from Broad to Specific**: When lost, zoom out. First, understand the solution structure by listing projects. Then, list files within a project. Finally, inspect specific files to understand their contents and dependencies.
    *   **Never Create Code to Justify a Hallucination**: If an assumption about a class name proves false, the solution is *never* to create an empty file with that name just to make a build pass. This compounds the error. The correct action is to discard the assumption and find the *actual* class that should be used.

### Zero-State Startup

The application must be able to start up successfully from a "zero state," meaning it can run without any pre-existing data or configuration files in the local AppData folder.

**Key Principles:**
-   **On-Demand Generation:** All necessary files, including identities, cryptographic keys, and certificates, must be generated on-demand if they do not exist.
-   **No Manual Setup:** The application should not require any manual setup steps or pre-configuration before its first run. This ensures a smooth user experience and simplifies deployment.
-   **Idempotent Startup:** The startup process should be idempotent. Running the application multiple times should not cause errors or unintended side effects.
