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

### 1. Start the Host

To run the application as a network host, use the `host` command. This will start the gRPC server and allow other peers to connect to you.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- host
```

The host will start and display a message like `Starting host...`. It is now ready to accept connections.

### 2. Connect to a Peer

To establish a secure session with a host, open a new terminal and use the `connect` command, providing the host's address and port.

-   `<host>`: The hostname or IP address of the peer (e.g., `localhost`).
-   `<port>`: The port the peer is listening on (default is typically 5000 or 5001, check the host's output).

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- connect localhost 5000
```

Upon success, the command will output a unique `Conversation ID`. **Save this ID**, as you will need it to send messages.

Example output:
```
Session established with peer. Conversation ID: 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d
```

### 3. Send a Message

Once a session is established, use the `send` command to send an encrypted message. You will need the `conversationId` from the previous step.

-   `<conversationId>`: The unique ID generated when you connected to the peer.
-   `<message>`: The plaintext message you want to send, enclosed in quotes.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- send 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello, world!"
```

The host peer's console will display the received message.

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
