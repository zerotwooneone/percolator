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

### 1. Building the Node

First, build the project from the `source` directory to create the executable. You only need to do this once, or whenever you make changes to the code.

```bash
dotnet build .\Percolator.Node\
```

All subsequent commands will use the compiled executable directly.

### 2. Start a Host & Get Connection Details

To run the application as a network host, use the `host` command. This will start the gRPC server and generate the connection details a peer needs to connect to you.

-   `--port` / `-p`: The port to listen on (default: `5000`).
-   `--identity` / `-i`: The name of the identity to use (default: `default`). A new identity will be created if it doesn't exist.

**Example:**
```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe host --identity Alice
```

The host will start and display the information a peer needs. **You must share the endpoint, peer name (`Alice`), and the full public key** with the peer who wants to connect.

```
Host started successfully.
Share the following details with the connecting peer:
- Peer Name: Alice
- Endpoint: localhost:5000
- Public Key: MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx...long_public_key...=
Share this link with peers who want to connect.
```

### 3. Connect to a Peer

To establish a secure session with a host, use the `connect` command with the details you received from them.

-   `<endpoint>`: The host and port of the peer (e.g., `localhost:5000`).
-   `--peer-name`: The identity name of the host (e.g., `Alice`).
-   `--remote-tls-key`: The full public key provided by the host.
-   `--identity` / `-i`: The name of your local identity.

**Example:**
```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe connect localhost:5000 --peer-name Alice --remote-tls-key "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx..." --identity Bob
```

Upon success, the command will output a unique `Conversation ID`. **Save this ID**, as you will need it to send messages.

### 4. Send a Message

Once a conversation is established, you can send encrypted messages.

-   `<message>`: The plaintext message to send.
-   `--identity` / `-i`: The name of the local identity to use.

#### Method 1: Send to a Known Conversation

Use the `conversation-id` from a previous `connect` command.

-   `--conversation-id`: The unique ID generated when you connected.

**Example:**
```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe send --conversation-id 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello, world!" --identity Bob
```

#### Method 2: Send to a Peer's Last Active Conversation

Use the peer's ID (which you can find in local application data after connecting).

-   `--peer-id`: The ID of the peer you want to message.

**Example:**
```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe send --peer-id 1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d "Hello again!" --identity Bob
```

#### Method 3: Connect and Send in One Step

You can establish a session and send a message in a single command using the host's connection details.

-   `--endpoint`: The host and port of the peer.
-   `--peer-name`: The identity name of the host.
-   `--remote-tls-key`: The full public key of the host.

**Example:**
```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe send "Hello from Bob!" --endpoint localhost:5000 --peer-name Alice --remote-tls-key "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx..." --identity Bob
```

## Example Scenario: Two Nodes on One Machine

This scenario demonstrates how to start two independent nodes and have one send a message to the other. You will need two separate terminal windows. All commands assume you are in the `source` directory.

**First, build the project:**
```bash
dotnet build .\Percolator.Node\
```

### Terminal 1: Start Node A (Host)

This node will act as the host with the identity `Alice`. It will generate connection details for Node B to use.

```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe host --identity Alice
```

After the host starts, **copy the `Peer Name`, `Endpoint`, and `Public Key`** that are displayed in the console.

### Terminal 2: Connect and Send Message with Node B

In a second terminal, use the `Bob` identity to connect to `Alice` and send a message in a single step.

**Step 1: Connect and Send**

Paste the `Endpoint`, `Peer Name`, and `Public Key` you copied from Terminal 1 into the command below.

```bash
.\Percolator.Node\bin\Debug\net8.0\Percolator.Node.exe send "Hello from Bob!" --endpoint localhost:5000 --peer-name Alice --remote-tls-key "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx..." --identity Bob
```

You should see the message appear in the console for Node A (Terminal 1).

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
