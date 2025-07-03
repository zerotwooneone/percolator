using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;

var rootCommand = new RootCommand("Percolator Node: A secure peer-to-peer communication tool.");

// *** Host Command ***
var hostCommand = new Command("host", "Starts the node, listens for peers, and hosts the gRPC service.");
rootCommand.AddCommand(hostCommand);

// *** Connect Command ***
var hostArgument = new Argument<string>("host", "The hostname or IP address of the peer.");
var portArgument = new Argument<int>("port", "The port of the peer's gRPC service.");
var connectCommand = new Command("connect", "Connects to a peer to establish a secure session.")
{
    hostArgument,
    portArgument
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var conversationIdArgument = new Argument<Guid>("conversationId", "The ID of the conversation to send the message to.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var sendCommand = new Command("send", "Sends an encrypted message to a peer over an established session.")
{
    conversationIdArgument,
    messageArgument
};
rootCommand.AddCommand(sendCommand);

// --- Dependency Injection Setup ---
var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((hostContext, services) =>
{
    var configuration = hostContext.Configuration;
    services.AddSingleton<IConfiguration>(configuration);
    services.AddApplicationServices(configuration);
    services.AddGrpcClient<TransportService.TransportServiceClient>();
});

var app = builder.Build();

// --- Command Handlers ---
hostCommand.SetHandler(async (InvocationContext context) =>
{
    Console.WriteLine("Starting host...");
    await app.RunAsync(context.GetCancellationToken());
});

connectCommand.SetHandler(async (InvocationContext context) =>
{
    var host = context.ParseResult.GetValueForArgument(hostArgument);
    var port = context.ParseResult.GetValueForArgument(portArgument);

    Console.WriteLine($"Connecting to {host}:{port}...");
    var activeIdentityContext = app.Services.GetRequiredService<ActiveIdentityContext>();
    var cryptoManager = app.Services.GetRequiredService<IX3DHManager>();
    var orchestrator = app.Services.GetRequiredService<X3DHOrchestrator>();
    var sessionManager = app.Services.GetRequiredService<DirectSessionManager>();
    var channel = GrpcChannel.ForAddress($"http://{host}:{port}");
    var client = new TransportService.TransportServiceClient(channel);

    // 1. Get local keys to create our bundle
    var localKeys = activeIdentityContext.Keys;
    if (localKeys is null)
    {
        Console.WriteLine("Could not find local identity. Please create one first.");
        return;
    }
    var signedPreKeyPublicBytes = localKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
    var signature = cryptoManager.SignPreKey(localKeys.IdentitySigningKey, signedPreKeyPublicBytes);
    var localBundle = new Percolator.Contracts.PreKeyBundle
    {
        IdentityKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
        SignedPreKey = ByteString.CopyFrom(signedPreKeyPublicBytes),
        PreKeySignature = ByteString.CopyFrom(signature),
        OneTimePreKey = ByteString.CopyFrom(localKeys.OneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
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

    var messageService = app.Services.GetRequiredService<IMessageService>();
    var content = new OpaqueContent(Encoding.UTF8.GetBytes(message));
    await messageService.SendDirectMessageAsync(new ConversationId(conversationId), content);
    Console.WriteLine("Message sent.");
});

// --- Run Application ---
return await rootCommand.InvokeAsync(args);
