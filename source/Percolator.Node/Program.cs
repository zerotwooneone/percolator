using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using Google.Protobuf.WellKnownTypes;
using Percolator.Application;
using Percolator.Application.KeyExchange;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Node;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;

// --- Main Entry Point ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = new ServiceCollection();
ConfigureServices(services, configuration);
var serviceProvider = services.BuildServiceProvider();

var rootCommand = BuildCommandLine(serviceProvider, args);
return await rootCommand.InvokeAsync(args);

// --- Service Configuration ---
static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddLogging(configure =>
    {
        configure.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    });

    services.AddSingleton(configuration);

    // Register all services from the Application layer via the single extension method
    services.AddApplicationServices(configuration);

    // Register host-specific services
    services.AddHostedService<MessageListenerService>();
}

// --- Command Line Interface Setup ---
static RootCommand BuildCommandLine(IServiceProvider serviceProvider, string[] args)
{
    var rootCommand = new RootCommand("Percolator Node");

    var hostCommand = new Command("host", "Host the node and listen for incoming connections");
    hostCommand.SetHandler(async context =>
    {
        var cancellationToken = context.GetCancellationToken();
        var logger = context.BindingContext.GetRequiredService<ILogger<Program>>();

        //This is a bit of a hack to ensure the identity is created before the host starts
        var activeIdentity = context.BindingContext.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentity.Certificate is null)
        {
            logger.LogInformation("No active identity found, creating a new one...");
            var identityService = context.BindingContext.GetRequiredService<IIdentityService>();
            await identityService.CreateIdentityAsync("percolator", cancellationToken);
        }

        logger.LogInformation("Hosting identity {thumbprint}", activeIdentity.Certificate.Thumbprint);

        await app.RunAsync(cancellationToken);
    });
    rootCommand.AddCommand(hostCommand);

    var connectCommand = new Command("connect", "Connect to a peer");
    var addressArgument = new Argument<string>("address", "The address of the peer to connect to");
    var portArgument = new Argument<int>("port", "The port of the peer to connect to");
    connectCommand.AddArgument(addressArgument);
    connectCommand.AddArgument(portArgument);
    connectCommand.SetHandler(async context =>
    {
        var address = context.ParseResult.GetValueForArgument(addressArgument);
        var port = context.ParseResult.GetValueForArgument(portArgument);
        var cancellationToken = context.GetCancellationToken();
        var logger = context.BindingContext.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Connecting to {address}:{port}...", address, port);

        var channel = GrpcChannel.ForAddress($"https://{address}:{port}");
        var client = new KeyExchange.KeyExchangeClient(channel);

        var orchestrator = context.BindingContext.GetRequiredService<X3DHOrchestrator>();
        var sessionManager = context.BindingContext.GetRequiredService<DirectSessionManager>();

        var remotePreKeyBundle = await client.GetPreKeyBundleAsync(new Empty(), cancellationToken: cancellationToken);

        var remotePeerId = new SessionPeerId(Guid.Parse(remotePreKeyBundle.PeerId));

        var initiationResult = orchestrator.InitiateHandshake(remotePeerId, remotePreKeyBundle);

        var response = await client.EstablishSessionAsync(initiationResult.Handshake, cancellationToken: cancellationToken);

        var activeIdentity = context.BindingContext.GetRequiredService<ActiveIdentityContext>();
        var localPeerId = new SessionPeerId(Guid.Parse(activeIdentity.IdentityName!));

        var conversationId = await sessionManager.EstablishSessionAsync(remotePeerId, initiationResult.SharedSecret, initiationResult.InitialRatchetPublicKey);

        logger.LogInformation("Session established with peer {peerId}. Conversation ID: {conversationId}", remotePeerId, conversationId);
    });
    rootCommand.AddCommand(connectCommand);

    var sendCommand = new Command("send", "Send a message to a peer");
    var conversationIdArgument = new Argument<Guid>("conversationId", "The ID of the conversation to send the message to");
    var messageArgument = new Argument<string>("message", "The message to send");
    sendCommand.AddArgument(conversationIdArgument);
    sendCommand.AddArgument(messageArgument);
    sendCommand.SetHandler(async context =>
    {
        var conversationId = new ConversationId(context.ParseResult.GetValueForArgument(conversationIdArgument));
        var message = context.ParseResult.GetValueForArgument(messageArgument);
        var cancellationToken = context.GetCancellationToken();
        var logger = context.BindingContext.GetRequiredService<ILogger<Program>>();

        var sessionManager = context.BindingContext.GetRequiredService<DirectSessionManager>();

        logger.LogInformation("Sending message to conversation {conversationId}...", conversationId);

        var encryptedMessage = await sessionManager.SendDirectMessageAsync(conversationId, message);

        logger.LogInformation("Message sent. Encrypted size: {size} bytes", encryptedMessage.Ciphertext.Length);
    });
    rootCommand.Add(sendCommand);

    return rootCommand;
}
