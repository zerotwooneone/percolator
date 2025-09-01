using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Dht;
using Percolator.Infrastructure.Identity;
using Percolator.Network;
using Percolator.Node;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;
using PeerId = Percolator.Identity.PeerId;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using MediatR;
using Percolator.Application.Cli;

var rootCommand = new RootCommand("Percolator Node: A secure peer-to-peer communication tool.");
const string defaultIdentityName = "default";

// *** Common Options ***
var selfIdentityOption = new Option<string>(
    new[] { "--selfIdentity" },
    getDefaultValue: () => "default",
    description: "The name of the local identity to use (defaults to 'default').");

// *** Host Command ***
var portOption = new Option<int?>(
    new[] { "--port", "-p" },
    description: "Optional port to listen on (overrides appsettings for this run)."
);

var hostCommand = new Command("host", "Starts the node, listens for peers, and hosts the gRPC service.")
{
    selfIdentityOption,
    portOption
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
    selfIdentityOption
};
rootCommand.AddCommand(dhtProbeCommand);

// *** Create Identity Command ***
var nameOption = new Option<string>(new[] { "--name" }, description: "Name of the identity to create")
{
    IsRequired = true
};
var createPeerIdOption = new Option<Guid?>(new[] { "--peer-id" }, description: "Optional PeerId to assign to the identity (defaults to a new GUID)");

var createIdentityCommand = new Command("create-identity", "Creates a local self identity and its key material (idempotent).")
{
    nameOption,
    createPeerIdOption
};
rootCommand.AddCommand(createIdentityCommand);

// Define handlers before registration

async Task CreateIdentityCommandHandler(InvocationContext context)
{
    var name = context.ParseResult.GetValueForOption(nameOption);
    var peerId = context.ParseResult.GetValueForOption(createPeerIdOption);
    var cancellationToken = context.GetCancellationToken();

    var services = CreateServiceProvider();
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
tlsDebugCommand.SetHandler(TlsDebugCommandHandler);
dhtProbeCommand.SetHandler(DhtProbeCommandHandler);
createIdentityCommand.SetHandler(CreateIdentityCommandHandler);

// --- Run Application ---
return await rootCommand.InvokeAsync(args);

// --- Handler Implementations ---

async Task HostCommandHandler(InvocationContext context)
{
    CancellationToken cancellationToken = context.GetCancellationToken();
    string? identityName = context.ParseResult.GetValueForOption(selfIdentityOption);

    // Step 1: Build a temporary service provider to get services needed for startup.
    var tempServices = new ServiceCollection();

    var tempConfigBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddNode();

    int? overridePort = context.ParseResult.GetValueForOption(portOption);
    if (overridePort.HasValue)
    {
        // Override Node:Port and compute Transport:GrpcPort = port + 1 for this process
        tempConfigBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Port"] = overridePort.Value.ToString(),
            ["Transport:GrpcPort"] = (overridePort.Value + 1).ToString()
        });
    }


    IConfigurationRoot tempConfig = tempConfigBuilder.Build();
    tempServices.AddLogging(builder => builder
        .AddConsole()
        .AddSimpleConsole(opt=>opt.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] "));
    tempServices.AddInfrastructureServices(tempConfig);
    tempServices.AddIdentityInfrastructure();
    tempServices.AddApplicationServices(tempConfig);
    tempServices.AddDhtInfrastructure();
    ServiceProvider tempServiceProvider = tempServices.BuildServiceProvider();

    try
    {
        // Ensure database schema is up to date BEFORE resolving identity using the temp provider
        await EnsureMigrationsAsync(tempServiceProvider, cancellationToken);

        // Get a logger for startup information
        var logger = tempServiceProvider.GetRequiredService<ILogger<Program>>();
        var nodeOptions = tempServiceProvider.GetRequiredService<IOptions<NodeOptions>>().Value;
        var transportOptions = tempServiceProvider.GetRequiredService<IOptions<TransportOptions>>().Value;
        int port = nodeOptions.Port;
        int grpcPort = transportOptions.GrpcPort;
        
        // Get the shared certificate manager
        var certificateManager = tempServiceProvider.GetRequiredService<SharedCertificateManager>();
        
        // Load identity for other features (but not for TLS)
        IIdentityOrchestrator tempIdentityOrchestrator = tempServiceProvider.GetRequiredService<IIdentityOrchestrator>();
        ActiveIdentityContext tempActiveIdentityContext = tempServiceProvider.GetRequiredService<ActiveIdentityContext>();
        await tempIdentityOrchestrator.ResolveIdentityAsync(identityName!, cancellationToken, defaultIdentityName);
        
        string publicKeyB64 = Convert.ToBase64String(tempActiveIdentityContext.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        // For simplicity in a local dev environment, we'll use localhost.
        // A more advanced implementation might try to discover the local network IP.
        InvitationLink invitationLink = new InvitationLink("localhost", port, publicKeyB64);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Host started successfully.");
        Console.WriteLine($"Invitation Link: {invitationLink}");
        Console.ResetColor();
        Console.WriteLine("Share this link with peers who want to connect.");
        
        // Configure and start the message listener service using our shared certificate
        logger.LogInformation("Starting MessageListenerService with shared certificate...");
        var messageListenerService = new MessageListenerService(
            tempServiceProvider.GetRequiredService<ILogger<MessageListenerService>>(),
            tempServiceProvider,
            certificateManager,
            tempServiceProvider.GetRequiredService<IOptions<TransportOptions>>());
            
        await messageListenerService.StartAsync(cancellationToken);
        logger.LogInformation("MessageListenerService started on port {Port}", grpcPort);

        // Step 3: Configure and build the main application using the pre-fetched certificate.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddNode();
        if (overridePort.HasValue)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Node:Port"] = port.ToString(),
                ["Transport:GrpcPort"] = grpcPort.ToString()
            });
        }
        builder.WebHost.UseKestrel(options =>
        {
            // Configure HTTPS endpoint with HTTP/2 only on the main port
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                // HTTP/2 only (not HTTP/1.1), no TLS for local development
                listenOptions.Protocols = HttpProtocols.Http2;
                logger.LogInformation("Configured HTTP/2 without TLS on port {Port}", port);
            });
        });

        builder.Services.AddInfrastructureServices(builder.Configuration);
        builder.Services.AddIdentityInfrastructure();
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddDhtInfrastructure();

        WebApplication app = builder.Build();

        // Apply database migrations conditionally based on environment
        await EnsureMigrationsAsync(app.Services, cancellationToken);

        // Step 4: Manually initialize the identity *again* using a scope from the main service provider
        // to ensure the ActiveIdentityContext is correct for the running application.
        using (var mainScope = app.Services.CreateScope())
        {
            var sp = mainScope.ServiceProvider;
            var identityOrchestrator = sp.GetRequiredService<IIdentityOrchestrator>();
            await identityOrchestrator.ResolveIdentityAsync(identityName!, cancellationToken, defaultIdentityName);

            // Initialize the in-memory peer trust store
            var peerTrustManager = sp.GetRequiredService<IPeerTrustManager>();
            peerTrustManager.Initialize();
        }

        await app.RunAsync(cancellationToken);

    }
    finally
    {
        await tempServiceProvider.DisposeAsync();
    }
}

async Task<int> DhtProbeCommandHandler(InvocationContext context)
{
    var endpointString = context.ParseResult.GetValueForArgument(endpointArgument);
    var targetIdentity = context.ParseResult.GetValueForOption(targetIdentityOption);
    var selfIdentity = context.ParseResult.GetValueForOption(selfIdentityOption);
    var cancellationToken = context.GetCancellationToken();

    if (!TryParseEndpoint(endpointString, out var endpoint))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid endpoint format: {endpointString}");
        Console.ResetColor();
        return 400;
    }

    var services = CreateServiceProvider();
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        // Ensure local identity is loaded; handler will use ActiveIdentityContext
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.ResolveIdentityAsync(selfIdentity!, cancellationToken, defaultIdentityName);

        // Delegate probing to MediatR handler which will resolve required services
        var mediator = serviceProvider.GetRequiredService<IMediator>();
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
            var idB64 = p.HasPeerId ? Convert.ToBase64String(p.PeerId.ToByteArray()) : "<none>";
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
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.ResolveIdentityAsync(identityName!, cancellationToken, defaultIdentityName);

        var mediator = serviceProvider.GetRequiredService<IMediator>();
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
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.ResolveIdentityAsync(identityName!, cancellationToken, defaultIdentityName);

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var activeIdentityContext = serviceProvider.GetRequiredService<ActiveIdentityContext>();
        logger.LogInformation("Sending with Identity: {IdentityName}:{PeerId}", identityName, activeIdentityContext.Identity!.Id);
        var mediator = serviceProvider.GetRequiredService<IMediator>();

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
        
        var directSessionId = await mediator.Send(new SendMessageCommand(endpoint, peerName, message), cancellationToken);
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Message sent successfully.  direct session {directSessionId.Value} with {peerName}");
        Console.ResetColor();
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
    var isDevelopment = hostEnv?.IsDevelopment() ?? string.Equals(aspnetEnv, "Development", StringComparison.OrdinalIgnoreCase);

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

static ServiceProvider CreateServiceProvider()
{
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder()
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
    services.AddHttpClient("percolator-grpc", client =>
    {
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        // Base address is not set here as it will be different for each peer
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        // Create a logger factory and logger instance that can be used within this scope
        var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<Program>();
        
        var handler = new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10), // Longer timeout for idle connections
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(20), // Longer connection timeout
            ResponseDrainTimeout = TimeSpan.FromSeconds(5), // More time to drain responses
            
            // Explicitly control connection pooling to prevent premature disposal
            PooledConnectionLifetime = TimeSpan.FromMinutes(30), // Longer connection lifetime
            
            SslOptions = new SslClientAuthenticationOptions
            {
                // Explicitly enable TLS 1.2 and 1.3
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                
                // Disable certificate revocation checking during debugging
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                
                // Use the custom certificate validation callback
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    if (certificate == null)
                    {
                        logger.LogError("CLIENT: No certificate provided by the server");
                        return false;
                    }

                    var cert = new X509Certificate2(certificate);
                    logger.LogInformation("CLIENT: TLS Certificate Validation: Subject={Subject}, Issuer={Issuer}, PolicyErrors={PolicyErrors}",
                        cert.Subject, cert.Issuer, sslPolicyErrors);
                    
                    logger.LogInformation("CLIENT: Certificate details - Thumbprint={Thumbprint}, NotBefore={NotBefore}, NotAfter={NotAfter}",
                        cert.Thumbprint, cert.NotBefore, cert.NotAfter);

                    // If there are policy errors, log details to help diagnose
                    if (sslPolicyErrors != SslPolicyErrors.None)
                    {
                        logger.LogWarning("CLIENT: Certificate validation errors: {Errors}", sslPolicyErrors);
                        
                        if ((sslPolicyErrors & SslPolicyErrors.RemoteCertificateChainErrors) != 0 && chain != null)
                        {
                            for (int i = 0; i < chain.ChainStatus.Length; i++)
                            {
                                logger.LogWarning("CLIENT: Chain error {Index}: {Status}, {Information}", 
                                    i, chain.ChainStatus[i].Status, chain.ChainStatus[i].StatusInformation);
                            }
                        }
                    }

                    // TEMPORARY FOR DEBUGGING: Accept any certificate to diagnose TOFU mechanism
                    logger.LogWarning("CLIENT: TEMPORARY DEBUG MODE: Accepting all certificates for TOFU debugging");
                    return true;

                    // In production, we'd use the peer trust manager:
                    // return peerTrustManager.IsTrusted(certificate);
                },
                
                // Client certificates will be selected by the callback
                LocalCertificateSelectionCallback = (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                {
                    logger.LogInformation("CLIENT: TLS Client Certificate Selection: Host={Host}, RemoteCertSubject={RemoteSubject}, LocalCerts={LocalCertCount}",
                        targetHost, 
                        remoteCertificate?.Subject ?? "null",
                        localCertificates?.Count ?? 0);
                    
                    // For now, return null (no client certificate)
                    // Later, we'll add client certificate selection logic
                    return null;
                }
            },
            // Increase timeouts to prevent premature connection closure
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false, // gRPC doesn't need cookies
            MaxConnectionsPerServer = 100 // Allow more concurrent connections
        };
        
        return handler;
    });

    var sp = services.BuildServiceProvider();
    // Centralized migration gate; synchronous wait is acceptable during startup
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

async Task TlsDebugCommandHandler(InvocationContext context)
{
    var host = (string)context.ParseResult.GetValueForArgument(tlsDebugCommand.Arguments[0]);
    var port = (int)context.ParseResult.GetValueForArgument(tlsDebugCommand.Arguments[1]);

    var serviceProvider = CreateServiceProvider(); // No identity needed for basic test
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
    
    Console.WriteLine("Starting TLS connectivity test...");
    var endpoint = new DnsEndPoint(host, port);
    
    // First run a basic TLS test
    await TlsDebugger.TestTlsHandshake(endpoint, logger);
    
    // If we have an active identity, try a mutual TLS test
    try 
    {
        var activeIdentity = serviceProvider.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentity.Identity != null)
        {
            var certService = serviceProvider.GetRequiredService<ITlsCertificateService>();
            var cert = await certService.GetOrCreateTlsCertificateAsync(
                activeIdentity.Identity.Name,
                activeIdentity.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());
                        
            Console.WriteLine("Testing mutual TLS with client certificate...");
            await TlsDebugger.TestMutualTlsHandshake(endpoint, cert, logger);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to run mutual TLS test");
    }
}
