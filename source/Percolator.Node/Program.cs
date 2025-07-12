using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

// --- Command Handlers ---

hostCommand.SetHandler(HostCommandHandler);
connectCommand.SetHandler(ConnectCommandHandler);
sendCommand.SetHandler(SendCommandHandler);

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
        // Step 2: Use the temporary provider to load the identity and then get the certificate object.
        IIdentityOrchestrator tempIdentityOrchestrator = tempServiceProvider.GetRequiredService<IIdentityOrchestrator>();
        ActiveIdentityContext tempActiveIdentityContext = tempServiceProvider.GetRequiredService<ActiveIdentityContext>();
        await tempIdentityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        ITlsCertificateService certificateService = tempServiceProvider.GetRequiredService<ITlsCertificateService>();
        X509Certificate2 serverCertificate = await certificateService.GetOrCreateTlsCertificateAsync(
            identityName!,
            tempActiveIdentityContext.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        string publicKeyB64 = Convert.ToBase64String(tempActiveIdentityContext.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        // For simplicity in a local dev environment, we'll use localhost.
        // A more advanced implementation might try to discover the local network IP.
        InvitationLink invitationLink = new InvitationLink("localhost", port, publicKeyB64);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Host started successfully.");
        Console.WriteLine($"Invitation Link: {invitationLink}");
        Console.ResetColor();
        Console.WriteLine("Share this link with peers who want to connect.");

        // Step 3: Configure and build the main application using the pre-fetched certificate.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Configuration.AddNode();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = serverCertificate;
                    httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                    httpsOptions.ClientCertificateValidation = (certificate, chain, policyErrors) =>
                    {
                        var logger = tempServiceProvider.GetRequiredService<ILogger<Program>>();
                        logger.LogInformation("Server: Performing client certificate validation for subject '{Subject}'", certificate.Subject);

                        var peerIdentityExtension = certificate.Extensions[Percolator.Cryptography.Oids.PeerIdentityKey];
                        if (peerIdentityExtension is null)
                        {
                            logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Missing the required peer identity extension.", certificate.Subject);
                            return false;
                        }

                        try
                        {
                            var reader = new AsnReader(peerIdentityExtension.RawData, AsnEncodingRules.DER);
                            var publicKey = reader.ReadOctetString();
                            if (publicKey.Length == 0)
                            {
                                logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Peer identity extension contains no public key.", certificate.Subject);
                                return false;
                            }

                            if (reader.HasData)
                            {
                                logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Peer identity extension contains unexpected trailing data.", certificate.Subject);
                                return false;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Server: Client certificate validation failed for '{Subject}'. Reason: Failed to decode peer identity extension.", certificate.Subject);
                            return false;
                        }

                        logger.LogInformation("Server: Client certificate validation successful for '{Subject}'.", certificate.Subject);
                        return true;
                    };
                });
            });
        });

        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddInfrastructureServices(builder.Configuration);
        builder.Services.AddGrpc();

        // Step 3.5: Configure Kestrel for Mutual TLS (mTLS)
        // We need to build a temporary service provider here to get the certificate service,
        // as Kestrel's configuration is finalized before the main app.Services provider is ready.
        var tempKestrelServices = new ServiceCollection();
        tempKestrelServices.AddLogging(); // Add logging services
        tempKestrelServices.AddApplicationServices(builder.Configuration);
        tempKestrelServices.AddInfrastructureServices(builder.Configuration);
        await using var tempKestrelProvider = tempKestrelServices.BuildServiceProvider();

        var identityOrchestratorForKestrel = tempKestrelProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestratorForKestrel.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        // After loading the identity, the ActiveIdentityContext is populated.
        var activeIdentityContextForKestrel = tempKestrelProvider.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentityContextForKestrel.Identity is null || activeIdentityContextForKestrel.Keys is null)
        {
            throw new InvalidOperationException("Failed to load identity context for Kestrel configuration.");
        }
        var publicSigningKey = activeIdentityContextForKestrel.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();

        var tlsCertificateService = tempKestrelProvider.GetRequiredService<ITlsCertificateService>();
        var serverCertificateForKestrel = await tlsCertificateService.GetOrCreateTlsCertificateAsync(activeIdentityContextForKestrel.Identity.Name, publicSigningKey);

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ConfigureHttpsDefaults(listenOptions =>
            {
                listenOptions.ServerCertificate = serverCertificateForKestrel;
                // DelayCertificate is crucial for our TOFU model. It establishes the TLS connection
                // and delegates certificate validation entirely to the application layer.
                listenOptions.ClientCertificateMode = ClientCertificateMode.DelayCertificate;
            });
        });

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
