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

## Platform Dependencies

### Windows Only

This application is currently **Windows-only**. This is because it relies on the `Percolator.Application` library, which uses the Windows Data Protection API (DPAPI) for securely storing cryptographic keys.

## Usage

The `Percolator.Node` executable is driven by a simple set of commands. The primary workflow involves one peer hosting a service and another peer sending them a message.

### Building the Node

First, build the project from the `source` directory to create the executable. You only need to do this once, or whenever you make changes to the code.

```bash
dotnet build .\Percolator.Node\
```

All subsequent commands will use the compiled executable directly.

---

## Primary Workflow: A Secure Chat in Two Steps

This example shows how to start two independent nodes (Alice and Bob) and have Bob send a message to Alice. All commands should be run from the `source` directory.

### Step 1: Alice Starts a Host

In one terminal, Alice runs the `host` command. This starts her node and generates an invitation link that Bob can use to connect.

```bash
# Terminal 1: Alice hosts
dotnet run --project .\Percolator.Node\ -- host --identity Alice
```

The host will start and display the invitation link. Alice copies this link and sends it to Bob.

```
Host started successfully.
Invitation Link: percolator://localhost:5000/MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEx...long_public_key...=
Share this link with peers who want to connect.
```

### Step 2: Bob Sends a Message to Alice

In a second terminal, Bob uses the `send` command. He provides Alice's endpoint and name. Because this is the first time he's connecting, the application will **Trust On First Use (TOFU)**, automatically saving Alice's certificate for future connections.

```bash
# Terminal 2: Bob sends a message
dotnet run --project .\Percolator.Node\ -- send "Hello, Alice!" --endpoint localhost:5000 --peer-name Alice --identity Bob
```

That's it! A secure session is established, the message is sent, and Bob's node now remembers Alice's identity for future conversations. You should see the message appear in Alice's console (Terminal 1).

---

## Command Reference

### `host`

Starts the node, listens for incoming connections, and hosts the gRPC service.

-   `--port` / `-p` (Optional): The port to listen on. Default: `5000`.
-   `--identity` / `-i` (Optional): The name of the identity to use. Default: `default`. A new identity will be created if it doesn't exist.

**Example:**
```bash
dotnet run --project .\Percolator.Node\ -- host --identity Alice
```

### `send`

Sends an encrypted message to a peer. Can also establish a new session if one doesn't exist.

-   `<message>` (Required): The plaintext message to send.
-   `--identity` / `-i` (Optional): The name of your local identity. Default: `default`.

**Sending to a New Peer (Trust On First Use):**

Use the peer's endpoint and name. The `--remote-tls-key` is not needed.

-   `--endpoint` (Required): The host and port of the peer (e.g., `localhost:5000`).
-   `--peer-name` (Required): The identity name of the peer you are messaging.

```bash
dotnet run --project .\Percolator.Node\ -- send "Hello!" --endpoint localhost:5000 --peer-name Alice --identity Bob
```

**Sending to an Existing Conversation:**

If you have already connected, you can send messages using the `conversation-id`.

-   `--conversation-id` (Required): The unique ID generated when you first connected.

```bash
dotnet run --project .\Percolator.Node\ -- send "Hello again!" --conversation-id <guid> --identity Bob
```

### `connect`

Establishes a secure session with a host without sending a message. This is useful if you only want to create the connection for later use.

-   `<endpoint>` (Required): The host and port of the peer (e.g., `localhost:5000`).
-   `--peer-name` (Required): The identity name of the host.
-   `--remote-tls-key` (Optional): The public key of the host. If omitted, the host's key will be trusted on first use. If provided, it will be verified against the key presented by the host.
-   `--identity` / `-i` (Optional): The name of your local identity.

**Example (Trust On First Use):**
```bash
dotnet run --project .\Percolator.Node\ -- connect localhost:5000 --peer-name Alice --identity Bob
```

Upon success, the command will output a unique `Conversation ID` for use in future `send` commands.

---

## Guidance for AI Assistants

*   **`System.CommandLine` Version**: The project is standardized on `System.CommandLine` version `2.0.0-beta4`. Do not upgrade to newer pre-release versions or introduce the `System.CommandLine.Hosting` package, as this led to significant instability and breaking changes.
*   **Manual Dependency Injection**: The application manually configures its own dependency injection container in `Program.cs`. It does not use the .NET Generic Host for command-line integration. Command handlers must resolve their dependencies from the `IServiceProvider` made available via the `InvocationContext`.
*   **Principle of Verification: Verify Before Acting**: To avoid hallucination, always verify the existence, name, and location of code artifacts (classes, methods, interfaces) using tools like `grep_search` and `list_dir` before attempting to use or modify them. Actions must be based on evidence from the codebase, not assumptions from training data.

### Zero-State Startup

The application must be able to start up successfully from a "zero state," meaning it can run without any pre-existing data or configuration files in the local AppData folder.

**Key Principles:**
-   **On-Demand Generation:** All necessary files, including identities, cryptographic keys, and certificates, must be generated on-demand if they do not exist.
-   **No Manual Setup:** The application should not require any manual setup steps or pre-configuration before its first run. This ensures a smooth user experience and simplifies deployment.
-   **Idempotent Startup:** The startup process should be idempotent. Running the application multiple times should not cause errors or unintended side effects.
