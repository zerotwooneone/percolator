# Percolator.Node

This project is the main executable entry point for a peer in the Percolator network.

## Purpose

The `Percolator.Node` application is responsible for bootstrapping and running all the necessary services for a peer to participate in the network. It integrates components from the various class libraries (`Application`, `Network`, etc.) into a runnable host.

### Key Responsibilities:

-   **Hosting**: Sets up and runs the ASP.NET Core host for the gRPC server.
-   **Service Startup**: Initializes and starts background services, most notably the `PeerDiscoveryService`.
-   **Configuration**: Manages application configuration (e.g., ports, settings).
-   **Entry Point**: Provides the `Main` method to launch the application.

## Operational Modes

The node is designed to support several operational modes to provide flexibility:

-   **Standard Mode**: The default mode where the node both broadcasts its presence and listens for other peers.
-   **Listen-Only Mode**: An optional mode where the node only listens for peer broadcasts without announcing its own presence.
-   **Interactive Mode**: A console-based interactive mode will be available for advanced users. This will allow for real-time commands and status checks while file transfers and other background processes continue to run seamlessly.

### Interactive Console Mode

In this mode, the node runs background services for peer discovery and file transfers while providing an interactive command-line interface for real-time control.

Available commands:
- `peers`: Lists all currently discovered peers on the network.
- `exit`: Gracefully shuts down the node and all background services.
