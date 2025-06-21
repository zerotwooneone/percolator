# Percolator.Node

This project is the main executable entry point for a peer in the Percolator network.

## Purpose

The `Percolator.Node` application is responsible for bootstrapping and running all the necessary services for a peer to participate in the network. It integrates components from the various class libraries (`Application`, `Network`, etc.) into a runnable host.

### Key Responsibilities:

-   **Hosting**: Sets up and runs the ASP.NET Core host for the gRPC server.
-   **Service Startup**: Initializes and starts background services, most notably the `PeerDiscoveryService`.
-   **Configuration**: Manages application configuration (e.g., ports, settings).
-   **Entry Point**: Provides the `Main` method to launch the application.
