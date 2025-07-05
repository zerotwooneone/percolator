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

### 1. Start a Host

To run the application as a network host, use the `host` command. This will start the gRPC server and allow other peers to connect to you. You must specify a unique identity for each host, and you can optionally specify a port.

-   `--port` / `-p`: The port to listen on (default: `5000`).
-   `--identity` / `-i`: The name of the identity to use (default: `default`). A new identity will be created if it doesn't exist.

**Example:**
```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- host --port 5000 --identity nodeA
```

The host will start and display a message like `Starting host on port 5000 with identity 'nodeA'...`. It is now ready to accept connections.

### 2. Connect to a Peer

To establish a secure session with a host, use the `connect` command. You must specify which local identity is initiating the connection.

-   `<host>`: The hostname or IP address of the peer (e.g., `localhost`).
-   `<port>`: The port the peer is listening on.
-   `--identity` / `-i`: The name of the local identity to use for the connection.

**Example:**
```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- connect localhost 5000 --identity nodeB
```

Upon success, the command will output a unique `Conversation ID`. **Save this ID**, as you will need it to send messages.

### 3. Send a Message

Once a session is established, use the `send` command to send an encrypted message. You must specify the `conversationId` and the identity associated with that conversation.

-   `<conversationId>`: The unique ID generated when you connected to the peer.
-   `<message>`: The plaintext message you want to send, enclosed in quotes.
-   `--identity` / `-i`: The name of the local identity that established the session.

**Example:**
```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- send 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello, world!" --identity nodeB
```

## Example Scenario: Two Nodes on One Machine

This scenario demonstrates how to start two independent nodes and have one send a message to the other. You will need three separate terminal windows.

### Terminal 1: Start Node A

This node will act as the initial host, listening for connections.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- host --port 5000 --identity nodeA
```

### Terminal 2: Start Node B

This node will also host, but it will be the one initiating the connection to Node A.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- host --port 5001 --identity nodeB
```

### Terminal 3: Initiate Connection and Send Message

Now, from a third terminal, we will perform the client actions using Node B's identity.

**1. Connect Node B to Node A:**

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- connect localhost 5000 --identity nodeB
```

After a moment, you will see a confirmation with a new Conversation ID. It will look something like this:
`Session established with peer. Conversation ID: 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d`

Copy this `Conversation ID`.

**2. Send a Message from Node B to Node A:**

Use the `send` command with the ID you just copied. Remember to specify that you are sending *from* `nodeB`'s identity.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- send <PASTE_YOUR_CONVERSATION_ID_HERE> "Hello from Node B!" --identity nodeB
```

You will see a "Message sent." confirmation in Terminal 3.

In **Terminal 1** (Node A's console), the received message will be displayed, confirming that the end-to-end communication was successful.

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
