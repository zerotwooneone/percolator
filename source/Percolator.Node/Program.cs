using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Globalization;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Infrastructure;
using Percolator.Network;
using Percolator.Node;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;

var rootCommand = new RootCommand("Percolator Node: A secure peer-to-peer communication tool.");

// *** Common Options ***
var identityOption = new Option<string>(
    new[] { "--identity", "-i" },
    getDefaultValue: () => "default",
    description: "The name of the identity to use.");

// *** Host Command ***
var portOption = new Option<int>(
    new[] { "--port", "-p" },
    getDefaultValue: () => 5000,
    description: "The port to listen on.");

var hostCommand = new Command("host", "Starts the node, listens for peers, and hosts the gRPC service.")
{
    portOption,
    identityOption
};
rootCommand.AddCommand(hostCommand);

// *** Connect Command ***
var endpointArgument = new Argument<string>("endpoint", "The endpoint of the peer (e.g., localhost:5000 or just localhost).");
var peerNameOption = new Option<string>("--peer-name", "The name of the peer to connect to.") { IsRequired = true };

var connectCommand = new Command("connect", "Connect to a peer and establish a session.")
{
    endpointArgument,
    peerNameOption,
    identityOption
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var conversationIdOption = new Option<Guid?>("--conversation-id", "The ID of the conversation. If omitted, the last active conversation with the target peer will be used.");
var peerIdOption = new Option<Guid?>("--peer-id", "The ID of the peer to send the message to. Required if conversation-id is not specified.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var endpointOption = new Option<string>("--endpoint", "The endpoint of the peer to establish a new session with before sending (e.g., localhost:5000 or just localhost).");

var sendCommand = new Command("send", "Send a message to a peer.")
{
    conversationIdOption,
    peerIdOption,
    messageArgument,
    endpointOption,
    peerNameOption,
    identityOption
};
rootCommand.AddCommand(sendCommand);

// *** TLS Debug Command ***
var tlsDebugCommand = new Command("tls-debug", "Tests basic TLS connectivity to a given endpoint.");
tlsDebugCommand.AddArgument(new Argument<string>("host", "The host to connect to."));
tlsDebugCommand.AddArgument(new Argument<int>("port", "The port to connect to."));
rootCommand.AddCommand(tlsDebugCommand);

// --- Command Handlers ---

hostCommand.SetHandler(HostCommandHandler);
connectCommand.SetHandler(ConnectCommandHandler);
sendCommand.SetHandler(SendCommandHandler);
tlsDebugCommand.SetHandler(TlsDebugCommandHandler);

// --- Run Application ---
return await rootCommand.InvokeAsync(args);

// --- Handler Implementations ---

async Task HostCommandHandler(InvocationContext context)
{
    CancellationToken cancellationToken = context.GetCancellationToken();
    int port = context.ParseResult.GetValueForOption(portOption);
    string? identityName = context.ParseResult.GetValueForOption(identityOption);

    // Step 1: Build a temporary service provider to get services needed for startup.
    var tempServices = new ServiceCollection();
    
    IConfigurationRoot tempConfig = new ConfigurationBuilder().AddNode().Build();
    tempServices.AddLogging(builder => builder.AddConsole());
    tempServices.AddApplicationServices(tempConfig);
    tempServices.AddInfrastructureServices(tempConfig);
    ServiceProvider tempServiceProvider = tempServices.BuildServiceProvider();

    try
    {
        // Get a logger for startup information
        var logger = tempServiceProvider.GetRequiredService<ILogger<Program>>();
        
        // Get the shared certificate manager
        var certificateManager = tempServiceProvider.GetRequiredService<SharedCertificateManager>();
        
        // Load identity for other features (but not for TLS)
        IIdentityOrchestrator tempIdentityOrchestrator = tempServiceProvider.GetRequiredService<IIdentityOrchestrator>();
        ActiveIdentityContext tempActiveIdentityContext = tempServiceProvider.GetRequiredService<ActiveIdentityContext>();
        await tempIdentityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);
        
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
        int grpcPort = port + 1; // We use port + 1 for gRPC service
        var messageListenerService = new MessageListenerService(
            tempServiceProvider.GetRequiredService<ILogger<MessageListenerService>>(),
            tempServiceProvider,
            certificateManager,
            grpcPort);
            
        await messageListenerService.StartAsync(cancellationToken);
        logger.LogInformation("MessageListenerService started on port {Port}", grpcPort);

        // Step 3: Configure and build the main application using the pre-fetched certificate.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Configuration.AddNode();
        builder.WebHost.UseKestrel(options =>
        {
            // Configure HTTP endpoint on the main port
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                
                // No HTTPS on this endpoint as it's for the web interface
                logger.LogInformation("Configured HTTP endpoint on port {Port}", port);
            });
        });

        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddInfrastructureServices(builder.Configuration);

        WebApplication app = builder.Build();

        // Step 4: Manually initialize the identity *again* using the main service provider
        // to ensure the ActiveIdentityContext is correct for the running application.
        IIdentityOrchestrator identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        // Initialize the in-memory peer trust store
        var peerTrustManager = app.Services.GetRequiredService<IPeerTrustManager>();
        peerTrustManager.Initialize();

        await app.RunAsync(cancellationToken);

    }
    finally
    {
        await tempServiceProvider.DisposeAsync();
    }
}

async Task ConnectCommandHandler(InvocationContext context)
{
    var endpointString = context.ParseResult.GetValueForArgument(endpointArgument);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var cancellationToken = context.GetCancellationToken();

    if (!TryParseEndpoint(endpointString, out var endpoint))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid endpoint format: {endpointString}");
        Console.ResetColor();
        return;
    }

    var services = CreateServiceProvider(identityName);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);
        
        var conversationService = serviceProvider.GetRequiredService<IConversationService>();
        var conversationId = await conversationService.CreateDirectConversationAsync(endpoint, peerName!);

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
    var conversationIdGuid = context.ParseResult.GetValueForOption(conversationIdOption);
    var peerIdGuid = context.ParseResult.GetValueForOption(peerIdOption);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var endpointString = context.ParseResult.GetValueForOption(endpointOption);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var cancellationToken = context.GetCancellationToken();

    var services = CreateServiceProvider(identityName);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        var conversationService = serviceProvider.GetRequiredService<IConversationService>();
        var messageService = serviceProvider.GetRequiredService<IMessageService>();

        ChatConversationId conversationId;

        if (conversationIdGuid.HasValue)
        {
            conversationId = new ChatConversationId(conversationIdGuid.Value);
        }
        else
        {
            if (string.IsNullOrEmpty(endpointString) || string.IsNullOrEmpty(peerName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Either --conversation-id or both --endpoint and --peer-name must be specified.");
                Console.ResetColor();
                return;
            }

            if (!TryParseEndpoint(endpointString, out var endpoint))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Invalid endpoint format: '{endpointString}'.");
                Console.ResetColor();
                return;
            }

            conversationId = await conversationService.CreateDirectConversationAsync(endpoint, peerName);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Established new conversation {conversationId.Value} with {peerName}");
            Console.ResetColor();
        }

        await messageService.SendDirectMessageAsync(conversationId, message);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Message sent successfully.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while sending the message: {ex.Message}");
        Console.ResetColor();
    }
}

static ServiceProvider CreateServiceProvider(string? identityName)
{
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddNode().Build();

    services.AddLogging(builder => builder.AddConsole().AddConfiguration(config.GetSection("Logging")));
    services.AddApplicationServices(config);
    services.AddInfrastructureServices(config);
    
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

    return services.BuildServiceProvider();
}

static ServiceProvider BuildServiceProvider(IConfiguration configuration)
{
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    services.AddInfrastructureServices(configuration);

    return services.BuildServiceProvider();
}

bool TryParseEndpoint(string? text, out DnsEndPoint? endpoint)
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

    var serviceProvider = CreateServiceProvider(null); // No identity needed for basic test
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
