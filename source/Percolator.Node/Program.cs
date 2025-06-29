using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Security;
using Percolator.Cryptography;
using Percolator.Identity;
using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Application;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Node;
using ServiceCollectionExtensions = Percolator.Application.Sessions.ServiceCollectionExtensions;

// --- Main Entry Point ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = ConfigureServices(configuration);
await using var serviceProvider = services.BuildServiceProvider();

var rootCommand = BuildCommandLine(serviceProvider, args);

await rootCommand.InvokeAsync(args);

// --- Service Configuration ---
static IServiceCollection ConfigureServices(IConfiguration configuration)
{
    var services = new ServiceCollection();
    services.AddLogging(configure =>
    {
        configure.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    });

    services.AddSingleton(configuration);

    // Register all domain services from the Application layer
    services.AddCryptography();
    services.AddIdentityServices();
    services.AddSessions();
    services.AddNetworkServices(configuration);
    services.AddPeerDiscovery();
    services.AddAppSecurity();
    services.AddApplicationServices(); // Add new application services

    services.AddGrpc();

    return services;
}

// --- Command Line Interface Setup ---
static RootCommand BuildCommandLine(IServiceProvider serviceProvider, string[] args)
{
    // --- Global Options ---
    var identityOption = new Option<string>(
        name: "--identity",
        description: "The name of the identity to use.",
        getDefaultValue: () => "default");

    // --- 'run' Command ---
    var portOption = new Option<int>(
        name: "--port",
        description: "The port to listen on.",
        getDefaultValue: () => 5001);
    var runCommand = new Command("run", "Run the Percolator node.");
    runCommand.AddOption(portOption);
    runCommand.AddOption(identityOption);
    runCommand.SetHandler(async (identityName, port) =>
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Service Configuration ---
        builder.Services.AddLogging(configure =>
        {
            configure.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        builder.Services.AddSingleton(builder.Configuration);
        builder.Services.AddCryptography();
        builder.Services.AddIdentityServices();
        builder.Services.AddSessions();
        builder.Services.AddNetworkServices(builder.Configuration);
        builder.Services.AddPeerDiscovery();
        builder.Services.AddAppSecurity();
        builder.Services.AddApplicationServices(); // Add new application services

        // Use the application layer hosted service to manage the discovery service's lifecycle
        builder.Services.AddHostedService<PeerDiscoveryHostedService>();
        builder.Services.AddHostedService<MessageListenerService>();

        var app = builder.Build();

        var identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadActiveIdentityAsync(identityName);

        var activeIdentity = app.Services.GetRequiredService<ActiveIdentityContext>();
        app.Urls.Add($"https://0.0.0.0:{port}");
        await app.RunAsync();

    }, identityOption, portOption);

    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddCommand(runCommand);
    rootCommand.AddGlobalOption(identityOption);

    // --- 'create-identity' Command ---
    var nameArgument = new Argument<string>("name", "The name of the identity to create.");
    var nicknameOption = new Option<string>("--nickname", "An optional nickname for the identity.");
    var createIdentityCommand = new Command("create-identity", "Create a new identity.");
    createIdentityCommand.AddArgument(nameArgument);
    createIdentityCommand.AddOption(nicknameOption);
    createIdentityCommand.SetHandler(async (name, nickname) =>
    {
        var identityService = serviceProvider.GetRequiredService<Percolator.Identity.IIdentityService>();
        var identityRecord = await identityService.CreateIdentityAsync(name, nickname);
        Console.WriteLine($"Identity created successfully:\n  Name: {identityRecord.Name}\n  Nickname: {identityRecord.Nickname}\n  Thumbprint: {identityRecord.Thumbprint}");
    }, nameArgument, nicknameOption);
    rootCommand.AddCommand(createIdentityCommand);

    // --- 'connect' Command ---
    var remotePeerIdArgument = new Argument<string>("remote-peer-id", "The PeerId of the remote peer.");
    var remotePreKeyBundleArgument = new Argument<string>("remote-prekey-bundle", "The Base64 encoded PreKeyBundle of the remote peer.");
    var connectCommand = new Command("connect", "Establish a direct session with a remote peer.");
    connectCommand.AddArgument(remotePeerIdArgument);
    connectCommand.AddArgument(remotePreKeyBundleArgument);
    connectCommand.SetHandler(async (remotePeerIdString, remotePreKeyBundleString) =>
    {
        var x3DhOrchestrator = serviceProvider.GetRequiredService<Percolator.Application.KeyExchange.X3DHOrchestrator>();
        var directSessionManager = serviceProvider.GetRequiredService<Percolator.Application.Sessions.DirectSessionManager>();
        var identityService = serviceProvider.GetRequiredService<Percolator.Identity.IIdentityService>();

        try
        {
            var remotePeerId = new Percolator.Sessions.PeerId(Guid.Parse(remotePeerIdString));
            var remotePreKeyBundle = new Percolator.Cryptography.PreKeyBundle(Convert.FromBase64String(remotePreKeyBundleString));

            // For simplicity, we'll assume initiator role for now.
            // In a real app, this would be more complex, potentially involving a server to exchange bundles.
            Console.WriteLine($"Attempting to establish session with {remotePeerId}...");

            var (sharedSecret, initialRatchetPublicKey, localPreKeyBundle) = await x3DhOrchestrator.InitiateHandshakeAsync(remotePeerId, remotePreKeyBundle);
            var conversationId = await directSessionManager.EstablishSessionAsync(remotePeerId, sharedSecret, initialRatchetPublicKey);

            var activeIdentity = await identityService.GetActiveIdentityAsync();
            Console.WriteLine($"Session established successfully with {remotePeerId}.");
            Console.WriteLine($"Your PeerId: {activeIdentity.PeerId}");
            Console.WriteLine($"Your PreKeyBundle (Base64): {Convert.ToBase64String(localPreKeyBundle.ToByteArray())}");
            Console.WriteLine($"ConversationId: {conversationId}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error establishing session: {ex.Message}");
            // Log full exception details if needed for debugging, but not to console for user
        }
    }, remotePeerIdArgument, remotePreKeyBundleArgument);
    rootCommand.AddCommand(connectCommand);

    // --- 'send' Command ---
    var conversationIdArgument = new Argument<string>("conversation-id", "The ID of the conversation.");
    var messageTextArgument = new Argument<string>("message-text", "The text message to send.");
    var sendCommand = new Command("send", "Send a message within an established session.");
    sendCommand.AddArgument(conversationIdArgument);
    sendCommand.AddArgument(messageTextArgument);
    sendCommand.SetHandler(async (conversationIdString, messageText) =>
    {
        var directSessionManager = serviceProvider.GetRequiredService<Percolator.Application.Sessions.DirectSessionManager>();
        var messageTransportService = serviceProvider.GetRequiredService<Percolator.Application.Network.IMessageTransportService>();
        var identityService = serviceProvider.GetRequiredService<Percolator.Identity.IIdentityService>();

        try
        {
            var conversationId = new Percolator.Sessions.ConversationId(Guid.Parse(conversationIdString));
            var activeIdentity = await identityService.GetActiveIdentityAsync();
            if (activeIdentity == null)
            {
                throw new InvalidOperationException("No active identity found to send message.");
            }

            var conversation = await directSessionManager.GetConversationAsync(conversationId);
            if (conversation == null)
            {
                Console.Error.WriteLine($"Error: Conversation with ID {conversationId} not found.");
                return;
            }

            // Determine recipient PeerId based on the conversation
            var recipientPeerId = conversation.LocalPeerId == activeIdentity.PeerId ? conversation.RemotePeerId : conversation.LocalPeerId;

            Console.WriteLine($"Sending message to conversation {conversationId}...");
            var ratchetMessage = await directSessionManager.SendMessageAsync(conversationId, messageText);
            await messageTransportService.SendMessageAsync(recipientPeerId, conversationId, ratchetMessage);
            Console.WriteLine("Message sent successfully.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error sending message: {ex.Message}");
            // Log full exception details if needed for debugging, but not to console for user
        }
    }, conversationIdArgument, messageTextArgument);
    rootCommand.AddCommand(sendCommand);

    return rootCommand;
}
