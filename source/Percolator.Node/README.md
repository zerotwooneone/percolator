# Percolator.Node

This project is the main executable entry point for a peer in the Percolator network.

## Purpose

The `Percolator.Node` application is responsible for bootstrapping and running all the necessary services for a peer to participate in the network. It integrates components from the various class libraries (`Application`, `Network`, etc.) into a runnable host.

### Key Responsibilities:

-   **Hosting**: Sets up and runs the ASP.NET Core host for the gRPC server.
-   **Service Startup**: Initializes and starts background services, such as peer discovery.
-   **Command-Line Interface**: Parses command-line arguments (e.g., `add-file`) to determine the application's behavior.
-   **Configuration**: Manages application configuration (e.g., ports, settings) and bootstraps all services using dependency injection.
-   **Secure Identity Management**: On first run, securely generates and stores a persistent user identity certificate.

## Operational Modes

The node is designed to support several operational modes to provide flexibility:

-   **Standard Mode**: The default mode where the node both broadcasts its presence and listens for other peers.
-   **Listen-Only Mode**: An optional mode where the node only listens for peer broadcasts without announcing its own presence.
-   **Interactive Mode**: A console-based interactive mode will be available for advanced users. This will allow for real-time commands and status checks while file transfers and other background processes continue to run seamlessly.

## Platform Dependencies

### Windows Only

This application is currently **Windows-only**. This is because it relies on the `Percolator.Application` library, which uses the Windows Data Protection API (DPAPI) for securely storing the identity certificate's password.

## Command-Line Interface (CLI)

The `Percolator.Node` executable is driven by command-line arguments, leveraging the `System.CommandLine` library. It can be run as a long-running node or used to execute one-off tasks.

### Running as a Node

To run the application as a standard network node, simply execute it without any commands. This will start the gRPC server and the peer discovery service, allowing it to communicate with other peers.

```bash
dotnet run --project .\Percolator.Node\Percolator.Node.csproj
```

### Available Commands

-   **`add-file <file-path>`**: Creates a manifest for the specified file or directory, signs it with the user's identity, and stores it locally. If no identity exists, this command will trigger the creation of a new secure identity certificate and password.
    -   Example: `dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- add-file C:\path\to\my_file.txt`
-   **`peers`**: Lists all currently discovered peers on the network.
-   **`exit`**: Gracefully shuts down the node and all background services.
