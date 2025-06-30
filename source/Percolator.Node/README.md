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
-   **`request-manifest <manifest-hash>`**: Requests a manifest from a running peer using its Base64-encoded hash.
    -   Example: `dotnet run --project .\Percolator.Node\Percolator.Node.csproj -- request-manifest 4cPQDexTSLrz6CSKErNk6ZD14/Rhxa+wrYsFhXw46iw=`

## Guidance for AI Assistants

This project has specific architectural patterns that must be followed to ensure stability and avoid common pitfalls.

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
