using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Dht;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Network.Trust;
using Percolator.Node;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using MediatR;
using Percolator.Application.Cli;
using Percolator.Prekey.DependencyInjection;
using Percolator.Infrastructure.MessageQueue;

var rootCommand = new RootCommand("Percolator Node: A secure peer-to-peer communication tool.");
const string defaultIdentityName = "default";

// *** Common Options ***
var selfIdentityOption = new Option<string>(
    new[] { "--selfIdentity" },
    getDefaultValue: () => "default",
    description: "The name of the local identity to use (defaults to 'default').");

// Optional DB filename override
var dbFileOption = new Option<string>(
    new[] { "--dbFile" },
    description: "Optional database filename to use instead of 'percolator.db' (filename only, combined with Storage:Path)."
);

// *** Host Command ***
var portOption = new Option<int?>(
    new[] { "--port", "-p" },
    description: "Optional port to listen on (overrides appsettings for this run)."
);

var hostCommand = new Command("host", "Starts the node, listens for peers, and hosts the gRPC service.")
{
    selfIdentityOption,
    portOption,
    dbFileOption
};
rootCommand.AddCommand(hostCommand);

// *** Connect Command ***
var endpointArgument = new Argument<string>("endpoint", "The endpoint of the peer (e.g., localhost:5000 or just localhost).");
var peerNameOption = new Option<string>("--peer-name", "The name of the peer to connect to.") { IsRequired = true };

var connectCommand = new Command("connect", "Connect to a peer and establish a session.")
{
    endpointArgument,
    peerNameOption,
    selfIdentityOption
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var peerIdOption = new Option<Guid?>("--peer-id", "The ID of the peer to send the message to. Required if conversation-id is not specified.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var endpointOption = new Option<string>("--endpoint", "The endpoint of the peer to establish a new session with before sending (e.g., localhost:5000 or just localhost).");

var sendCommand = new Command("send", "Send a message to a peer.")
{
    peerIdOption,
    messageArgument,
    endpointOption,
    peerNameOption,
    selfIdentityOption
};
rootCommand.AddCommand(sendCommand);

// *** TLS Debug Command ***
var tlsDebugCommand = new Command("tls-debug", "Tests basic TLS connectivity to a given endpoint.");
tlsDebugCommand.AddArgument(new Argument<string>("host", "The host to connect to."));
tlsDebugCommand.AddArgument(new Argument<int>("port", "The port to connect to."));
rootCommand.AddCommand(tlsDebugCommand);

// *** DHT Probe Command ***
var targetIdentityOption = new Option<string>(new[] { "--target-identity", "-i" }, "The target identity name at the remote peer.")
{
    IsRequired = true
};

var dhtProbeCommand = new Command("dht-probe", "Send a DHT Ping then FindNode against a peer endpoint using hashed local identity signing key.")
{
    endpointArgument,
    targetIdentityOption,
    selfIdentityOption,
    dbFileOption
};
rootCommand.AddCommand(dhtProbeCommand);

// *** Submit Prekeys Command ***
var prekeyCountOption = new Option<int>(new[] { "--count" }, () => 5, "Number of one-time prekeys to include (default 5)");
var prekeyExpiresDaysOption = new Option<int>(new[] { "--expires-days" }, () => 365, "Days until expiration from now (default 365)");

var submitPrekeysCommand = new Command("submit-prekeys", "Generate and submit a signed prekey and multiple one-time prekeys to a peer.")
{
    targetIdentityOption,
    selfIdentityOption,
    dbFileOption,
    prekeyCountOption,
    prekeyExpiresDaysOption
};
rootCommand.AddCommand(submitPrekeysCommand);

// *** Create Identity Command ***
var nameOption = new Option<string>(new[] { "--name" }, description: "Name of the identity to create")
{
    IsRequired = true
};
var createPeerIdOption = new Option<Guid?>(new[] { "--peer-id" }, description: "Optional PeerId to assign to the identity (defaults to a new GUID)");

var createIdentityCommand = new Command("create-identity", "Creates a local self identity and its key material (idempotent).")
{
    nameOption,
    createPeerIdOption,
    dbFileOption
};
rootCommand.AddCommand(createIdentityCommand);

// Define handlers before registration

async Task CreateIdentityCommandHandler(InvocationContext context)
{
    var name = context.ParseResult.GetValueForOption(nameOption);
    var peerId = context.ParseResult.GetValueForOption(createPeerIdOption);
    var dbFile = context.ParseResult.GetValueForOption(dbFileOption);
    var cancellationToken = context.GetCancellationToken();

    // Build configuration with optional dbFile override
    var configBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddNode();
    if (!string.IsNullOrWhiteSpace(dbFile))
    {
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Percolator:DatabaseFileName"] = dbFile
        });
    }
    
    var config = configBuilder.Build();

    var services = CreateServiceProvider(config);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        var id = await mediator.Send(new CreateSelfIdentityCommand(name!, peerId), cancellationToken);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Identity '{name}' ensured. SelfIdentityId={id}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while creating identity: {ex.Message}");
        Console.ResetColor();
    }
}

// --- Command Handlers ---

hostCommand.SetHandler(HostCommandHandler);
connectCommand.SetHandler(ConnectCommandHandler);
sendCommand.SetHandler(SendCommandHandler);
dhtProbeCommand.SetHandler(DhtProbeCommandHandler);
createIdentityCommand.SetHandler(CreateIdentityCommandHandler);
submitPrekeysCommand.SetHandler(SubmitPrekeysCommandHandler);

// --- Run Application ---
return await rootCommand.InvokeAsync(args);

// --- Handler Implementations ---

async Task<int> SubmitPrekeysCommandHandler(InvocationContext context)
{
    var targetIdentity = context.ParseResult.GetValueForOption(targetIdentityOption);
    var selfIdentity = context.ParseResult.GetValueForOption(selfIdentityOption);
    var dbFile = context.ParseResult.GetValueForOption(dbFileOption);
    var count = context.ParseResult.GetValueForOption(prekeyCountOption);
    var expiresDays = context.ParseResult.GetValueForOption(prekeyExpiresDaysOption);
    var cancellationToken = context.GetCancellationToken();

    // Build configuration with optional dbFile override
    var configBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddNode();
    if (!string.IsNullOrWhiteSpace(dbFile))
    {
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Percolator:DatabaseFileName"] = dbFile
        });
    }
    var cfg = configBuilder.Build();

    var services = CreateServiceProvider(cfg);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new HostCommand(selfIdentity!), cancellationToken);
        var expiresUtc = DateTimeOffset.UtcNow.AddDays(expiresDays);
        Console.WriteLine($"Submitting prekeys to {targetIdentity} for (count={count}, expires={expiresUtc:u})...");
        var rc = await mediator.Send(new Percolator.Application.Cli.SubmitPreKeysCommand(targetIdentity!, count, expiresUtc), cancellationToken);

        if (rc == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Prekeys submitted successfully.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Submit prekeys failed with code {rc}.");
            Console.ResetColor();
        }
        return rc;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while submitting prekeys: {ex.Message}");
        Console.ResetColor();
        return 500;
    }
}

async Task HostCommandHandler(InvocationContext context)
{
    CancellationToken cancellationToken = context.GetCancellationToken();
    string? identityName = context.ParseResult.GetValueForOption(selfIdentityOption);
    string? dbFile = context.ParseResult.GetValueForOption(dbFileOption);
    int? overridePort = context.ParseResult.GetValueForOption(portOption);

    var builder = Host.CreateDefaultBuilder()
        .ConfigureAppConfiguration((hostingContext, config) =>
        {
            config.AddJsonFile("appsettings.json", optional: true);
            config.AddNode();

            var overrides = new Dictionary<string, string?>();
            if (overridePort.HasValue)
            {
                overrides["Node:Port"] = overridePort.Value.ToString();
            }
            if (!string.IsNullOrWhiteSpace(dbFile))
            {
                overrides["Percolator:DatabaseFileName"] = dbFile;
            }
            if (overrides.Count > 0)
            {
                config.AddInMemoryCollection(overrides);
            }
        })
        .ConfigureLogging(logging =>
        {
            logging.AddSimpleConsole(opt => opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ");
        })
        .ConfigureServices((hostingContext, services) =>
        {
            var config = hostingContext.Configuration;
            services.AddInfrastructureServices(config);
            services.AddIdentityInfrastructure();
            services.AddApplicationServices(config);
            services.AddPrekey();
            services.AddDhtInfrastructure();
            services.AddMessageQueueInfrastructure();
        });

    var host = builder.Build();

    try
    {
        // Ensure database schema is up to date
        await EnsureMigrationsAsync(host.Services, cancellationToken);

        // Initialize the in-memory peer trust store
        var peerTrustManager = host.Services.GetRequiredService<IPeerTrustManager>();
        peerTrustManager.Initialize();

        // Start background services (including GrpcShutdownHostedService)
        await host.StartAsync(cancellationToken);

        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        // Use MediatR to resolve identity.
        // This triggers ActiveIdentityLoadedEvent, which tells GrpcServerManager to start the gRPC listener.
        var mediator = host.Services.GetRequiredService<IMediator>();
        var startupInfo = await mediator.Send(new HostCommand(identityName!), cancellationToken);
        
        var nodeOptions = host.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
        int port = nodeOptions.Port;

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Host started successfully.");
        Console.WriteLine($"Invitation: host=localhost port={port} key={startupInfo.PublicKeyB64}");
        Console.ResetColor();
        Console.WriteLine("Share this link with peers who want to connect.");

        // Wait for shutdown (e.g. Ctrl+C)
        await host.WaitForShutdownAsync(cancellationToken);
    }
    catch (Exception ex)
    {
        var logger = host.Services.GetService<ILogger<Program>>();
        if (logger != null)
        {
            logger.LogError(ex, "An error occurred while hosting the node.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"An error occurred while hosting the node: {ex.Message}");
            Console.ResetColor();
        }
    }
    finally
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            host.Dispose();
        }
    }
}

async Task<int> DhtProbeCommandHandler(InvocationContext context)
{
    var endpointString = context.ParseResult.GetValueForArgument(endpointArgument);
    var targetIdentity = context.ParseResult.GetValueForOption(targetIdentityOption);
    var selfIdentity = context.ParseResult.GetValueForOption(selfIdentityOption);
    var dbFile = context.ParseResult.GetValueForOption(dbFileOption);
    var cancellationToken = context.GetCancellationToken();

    if (!TryParseEndpoint(endpointString, out var endpoint))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid endpoint format: {endpointString}");
        Console.ResetColor();
        return 400;
    }

    // Build configuration with optional dbFile override
    var configBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddNode();
    if (!string.IsNullOrWhiteSpace(dbFile))
    {
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Percolator:DatabaseFileName"] = dbFile
        });
    }
    var probeConfig = configBuilder.Build();

    var services = CreateServiceProvider(probeConfig);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        // Delegate probing to MediatR handler which will resolve required services
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        
        mediator.Send(new HostCommand(selfIdentity!), cancellationToken);
        
        Console.WriteLine($"Probing {endpoint} with self='{selfIdentity}', target='{targetIdentity}'...");
        var response = await mediator.Send(new DhtProbeCommand(endpoint!, targetIdentity!, selfIdentity), cancellationToken);

        var peers = response?.CloserPeers ?? new Google.Protobuf.Collections.RepeatedField<Percolator.Contracts.NodeInfo>();
        if (peers.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No closer peers were returned.");
            Console.ResetColor();
            return 0;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Returned {peers.Count} peers:");
        Console.ResetColor();
        foreach (var p in peers)
        {
            var idB64 = p.HasPeerId ? p.PeerId.ToString() : "<none>";
            Console.WriteLine($"- {p.Address}  id={idB64}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while probing: {ex.Message}");
        Console.ResetColor();
        return 500; 
    }
}

async Task ConnectCommandHandler(InvocationContext context)
{
    var endpointString = context.ParseResult.GetValueForArgument(endpointArgument);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(selfIdentityOption);
    var cancellationToken = context.GetCancellationToken();

    if (!TryParseEndpoint(endpointString, out var endpoint))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid endpoint format: {endpointString}");
        Console.ResetColor();
        return;
    }

    var services = CreateServiceProvider();
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new HostCommand(identityName!), cancellationToken);

        var conversationId = await mediator.Send(new ConnectToPeerCommand(endpoint, peerName!), cancellationToken);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully connected to {peerName} and created conversation {conversationId.Value}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while connecting: {ex.Message}");
        Console.ResetColor();
    }
}

async Task SendCommandHandler(InvocationContext context)
{
    var peerIdGuid = context.ParseResult.GetValueForOption(peerIdOption);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var endpointString = context.ParseResult.GetValueForOption(endpointOption);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(selfIdentityOption);
    var cancellationToken = context.GetCancellationToken();

    var services = CreateServiceProvider();
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var mediator = serviceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new HostCommand(identityName!), cancellationToken);

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var activeIdentityContext = serviceProvider.GetRequiredService<ActiveIdentityContext>();
        logger.LogInformation("Sending with Identity: {IdentityName}:{PeerId}", identityName, activeIdentityContext.Identity!.Id);
        
        if (string.IsNullOrEmpty(endpointString) || string.IsNullOrEmpty(peerName))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Either --conversation-id or both --endpoint and --peer-name must be specified.");
            Console.ResetColor();
            return;
        }

        //todo: need to handle getting the peer and endpoint by peer name
        if (!TryParseEndpoint(endpointString, out var endpoint))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid endpoint format: '{endpointString}'.");
            Console.ResetColor();
            return;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("This is not implemented!");
        Console.ResetColor();
        
        
        // Console.ForegroundColor = ConsoleColor.Green;
        // Console.WriteLine($"Message sent successfully.  direct session {directSessionId.Value} with {peerName}");
        // Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while sending the message: {ex.Message}");
        Console.ResetColor();
    }
}

static async Task EnsureMigrationsAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
{
    using var scope = serviceProvider.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<Percolator.Infrastructure.Persistence.PercolatorDbContext>();
    var hostEnv = scope.ServiceProvider.GetService<IHostEnvironment>();
    var aspnetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    // Prefer IHostEnvironment if available; fallback to env var if not
    //todo: fix hostEnv
    var isDevelopment = true; //;hostEnv?.IsDevelopment() ?? string.Equals(aspnetEnv, "Development", StringComparison.OrdinalIgnoreCase);

    if (isDevelopment)
    {
        try
        {
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("PendingModelChangesWarning", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "EF Core detected pending model changes that are not captured by any migration. " +
                "Add a new migration, then update the database. For example:\n" +
                "  dotnet ef migrations add SyncModel -p .\\Percolator.Infrastructure\\Percolator.Infrastructure.csproj -s .\\Percolator.Node\\Percolator.Node.csproj --context Percolator.Infrastructure.Persistence.PercolatorDbContext\n" +
                "  dotnet ef database update -p .\\Percolator.Infrastructure\\Percolator.Infrastructure.csproj -s .\\Percolator.Node\\Percolator.Node.csproj\n", ex);
        }
    }
    else
    {
        var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
        {
            throw new InvalidOperationException(
                "Database schema is out of date. Pending migrations detected: " +
                string.Join(", ", pending) +
                ". Please run EF migrations (e.g., 'dotnet ef database update') before starting the node.");
        }
    }
}

static ServiceProvider CreateServiceProvider(IConfiguration? configuration = null)
{
    var services = new ServiceCollection();
    var config = configuration ?? new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddNode()
        .Build();

    services.AddLogging(builder => builder
        .AddConsole()
        .AddSimpleConsole(opt=>opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ")
        .AddConfiguration(config.GetSection("Logging")));
    services.AddInfrastructureServices(config);
    services.AddIdentityInfrastructure();
    services.AddApplicationServices(config);
    services.AddDhtInfrastructure();

    var sp = services.BuildServiceProvider();
    EnsureMigrationsAsync(sp, CancellationToken.None).GetAwaiter().GetResult();
    return sp;
}

bool TryParseEndpoint(string? text, [NotNullWhen(true)] out DnsEndPoint? endpoint)
{
    endpoint = null;
    if (string.IsNullOrEmpty(text))
        return false;

    var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
    string host;
    int port;

    switch (parts.Length)
    {
        case 1: // Host only, use default port
            host = parts[0];
            port = 5000; // Default port
            break;
        case 2: // Host and port
            host = parts[0];
            if (!int.TryParse(parts[1], out port))
                return false;
            break;
        default:
            return false;
    }

    endpoint = new DnsEndPoint(host, port);
    return true;
}
