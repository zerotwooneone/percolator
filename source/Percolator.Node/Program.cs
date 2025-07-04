using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Net.Http;
using System.Text;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Cryptography;
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
    conversationIdArgument,
    messageArgument,
    identityOption // Add identity option to client commands
};
rootCommand.AddCommand(sendCommand);

// --- Command Handlers ---

hostCommand.SetHandler(async (InvocationContext context) =>
{
    var port = context.ParseResult.GetValueForOption(portOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Create a temporary service provider to get the certificate before the host starts
    var tempServices = new ServiceCollection();
    var tempConfig = new ConfigurationBuilder().Build();
    tempServices.AddApplicationServices(tempConfig);
    await using var tempServiceProvider = tempServices.BuildServiceProvider();

    var certificateService = tempServiceProvider.GetRequiredService<ITlsCertificateService>();
    var certificate = await certificateService.GetOrCreateTlsCertificateAsync(identityName!);

    var builder = WebApplication.CreateBuilder(args);

    // Configure Kestrel with the certificate
    builder.WebHost.UseKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, port, listenOptions =>
        {
            listenOptions.UseHttps(certificate);
        });
    });

    // Configure Services for the main application
    builder.Services.AddApplicationServices(builder.Configuration);
    builder.Services.AddGrpc();

    var app = builder.Build();

    // Configure Middleware
    app.UseRouting();
    app.MapGrpcService<PercolatorMessageService>();

    Console.WriteLine($"Starting HTTPS host on port {port} with identity '{identityName}'...");
    await app.RunAsync(context.GetCancellationToken());
});

connectCommand.SetHandler(async (InvocationContext context) =>
{
    var host = context.ParseResult.GetValueForArgument(hostArgument);
    var port = context.ParseResult.GetValueForArgument(portArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build(); // Empty config, as services don't seem to use it heavily
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
        handshakeResult.SharedSecret,
        handshakeResult.InitialRatchetPublicKey
    );

    Console.WriteLine($"Session established with peer. Conversation ID: {conversationId}");
});

sendCommand.SetHandler(async (InvocationContext context) =>
{
    var conversationId = context.ParseResult.GetValueForArgument(conversationIdArgument);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build();
    services.AddApplicationServices(configuration);
    
    await using var serviceProvider = services.BuildServiceProvider();

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var sessionManager = serviceProvider.GetRequiredService<DirectSessionManager>();
    await sessionManager.SendMessageAsync(new ConversationId(conversationId), message);
    Console.WriteLine("Message sent.");
});

// --- Run Application ---
return await rootCommand.InvokeAsync(args);
