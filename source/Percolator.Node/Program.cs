using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;

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
var hostArgument = new Argument<string>("host", "The hostname or IP address of the peer.");
var portArgument = new Argument<int>("port", "The port of the peer's gRPC service.");
var connectCommand = new Command("connect", "Connects to a peer to establish a secure session.")
{
    hostArgument,
    portArgument,
    identityOption // Add identity option to client commands
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var conversationIdArgument = new Argument<Guid>("conversationId", "The ID of the conversation to send the message to.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var sendCommand = new Command("send", "Sends an encrypted message to a peer over an established session.")
{
    hostArgument, // Re-use host and port arguments for sending
    portArgument,
    conversationIdArgument,
    messageArgument,
    identityOption // Add identity option to client commands
};
rootCommand.AddCommand(sendCommand);

// --- Command Handlers ---

hostCommand.SetHandler(async (InvocationContext context) =>
{
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var port = context.ParseResult.GetValueForOption(portOption);
    var cancellationToken = context.GetCancellationToken();

    // Step 1: Create a temporary, but complete, service provider to resolve the TLS certificate.
    // This breaks the circular dependency between Kestrel configuration and service initialization.
    var tempServices = new ServiceCollection();
    tempServices.AddLogging(b => b.AddConsole());
    tempServices.AddApplicationServices(new ConfigurationBuilder().Build());
    await using var tempServiceProvider = tempServices.BuildServiceProvider();

    // Step 2: Use the temporary provider to load the identity and then get the certificate.
    var tempIdentityOrchestrator = tempServiceProvider.GetRequiredService<IIdentityOrchestrator>();
    await tempIdentityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);
    var certificateService = tempServiceProvider.GetRequiredService<ITlsCertificateService>();
    var serverCertificate = await certificateService.GetOrCreateTlsCertificateAsync(identityName!); 

    // Step 3: Configure and build the main application using the pre-fetched certificate.
    var builder = WebApplication.CreateBuilder();

    builder.WebHost.UseKestrel(options =>
    {
        options.Listen(IPAddress.Any, port, listenOptions =>
        {
            listenOptions.UseHttps(serverCertificate);
        });
    });

    builder.Logging.ClearProviders().AddConsole();
    builder.Services.AddApplicationServices(builder.Configuration);
    builder.Services.AddGrpc();

    var app = builder.Build();

    // Step 4: Manually initialize the identity *again* using the main service provider
    // to ensure the ActiveIdentityContext is correct for the running application.
    var identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

    // Step 5: Configure and run the application.
    app.MapGrpcService<PercolatorMessageService>();
    await app.RunAsync(cancellationToken);
});

connectCommand.SetHandler(async (InvocationContext context) =>
{
    var host = context.ParseResult.GetValueForArgument(hostArgument);
    var port = context.ParseResult.GetValueForArgument(portArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build(); // Empty config, as services don't seem to use it heavily
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);

    // This is INSECURE and for development purposes only.
    // In a real-world scenario, you would implement proper certificate pinning
    // or a custom validation callback that checks the peer's public key.
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };

    services.AddGrpcClient<TransportService.TransportServiceClient>(o =>
    {
        o.Address = new Uri($"https://{host}:{port}");
    })
    .ConfigurePrimaryHttpMessageHandler(() => handler);
    
    await using var serviceProvider = services.BuildServiceProvider();

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    Console.WriteLine($"Connecting to {host}:{port}...");
    var activeIdentityContext = serviceProvider.GetRequiredService<ActiveIdentityContext>();
    var cryptoManager = serviceProvider.GetRequiredService<IX3DHManager>();
    var orchestrator = serviceProvider.GetRequiredService<X3DHOrchestrator>();
    var sessionManager = serviceProvider.GetRequiredService<DirectSessionManager>();
    var client = serviceProvider.GetRequiredService<TransportService.TransportServiceClient>();

    // 1. Get local keys to create our bundle
    var localKeys = activeIdentityContext.Keys;
    if (localKeys is null)
    {
        Console.WriteLine("Could not find local identity. Please create one first.");
        return;
    }
    if (localKeys.OneTimePreKeys is null || localKeys.OneTimePreKeys.Length == 0)
    {
        Console.WriteLine("Could not find any one-time pre-keys for the local identity.");
        return;
    }
    var signedPreKeyPublicBytes = localKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
    var signature = cryptoManager.SignPreKey(localKeys.IdentitySigningKey, signedPreKeyPublicBytes);
    var localBundle = new Percolator.Contracts.PreKeyBundle
    {
        IdentityKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
        SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
        PreKeySignature = ByteString.CopyFrom(signature),
        OneTimePreKey = ByteString.CopyFrom(localKeys.OneTimePreKeys[0].PublicKey.ExportSubjectPublicKeyInfo())
    };

    // 2. Call the remote peer to establish a session
    var request = new EstablishSessionRequest { InitiatorBundle = localBundle };
    var response = await client.EstablishSessionAsync(request);

    // 3. Use the response bundle to complete the handshake locally
    var remotePeerId = new SessionPeerId(new Guid(response.SessionId)); // This assumes the SessionId is the PeerId. A better approach would be to return the PeerId explicitly.
    var handshakeResult = orchestrator.InitiateHandshake(remotePeerId, response.ResponderBundle);

    // 4. Create the secure session
    var conversationId = await sessionManager.EstablishSessionAsync(
        remotePeerId,
        new OpaquePublicKey(response.ResponderBundle.IdentityKey.ToByteArray()),
        handshakeResult.SharedSecret
    );

    Console.WriteLine($"Session established with peer. Conversation ID: {conversationId}");
});

sendCommand.SetHandler(async (InvocationContext context) =>
{
    var host = context.ParseResult.GetValueForArgument(hostArgument);
    var port = context.ParseResult.GetValueForArgument(portArgument);
    var conversationId = context.ParseResult.GetValueForArgument(conversationIdArgument);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    
    // This is INSECURE and for development purposes only.
    var handler = new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    };

    services.AddGrpcClient<TransportService.TransportServiceClient>(o =>
    {
        o.Address = new Uri($"https://{host}:{port}");
    })
    .ConfigurePrimaryHttpMessageHandler(() => handler);

    await using var serviceProvider = services.BuildServiceProvider();

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var client = serviceProvider.GetRequiredService<TransportService.TransportServiceClient>();

    var request = new DeliverOpaqueMessageRequest
    {
        SessionId = conversationId.ToString(),
        Payload = ByteString.CopyFromUtf8(message)
    };

    Console.WriteLine($"Sending message to conversation {conversationId}...");
    await client.DeliverOpaqueMessageAsync(request);
    Console.WriteLine("Message sent.");
});

// --- Run Application ---
return await rootCommand.InvokeAsync(args);
