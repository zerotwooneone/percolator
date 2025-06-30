using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Percolator.Application;
using Percolator.Application.KeyExchange;
using Percolator.Application.Identity;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using Percolator.Node;
using Percolator.Sessions;
using SessionPeerId = Percolator.Sessions.PeerId;
using DomainIdentityService = Percolator.Identity.IIdentityService;

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
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        //This is a bit of a hack to ensure the identity is created before the host starts
        var activeIdentity = serviceProvider.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentity.Certificate is null)
        {
            logger.LogInformation("No active identity found, creating a new one...");
            var identityService = serviceProvider.GetRequiredService<DomainIdentityService>();
            await identityService.CreateIdentityAsync("percolator", null, cancellationToken);
        }

        logger.LogInformation("Hosting identity {thumbprint}", activeIdentity.Certificate?.Thumbprint);

        var hostedServices = serviceProvider.GetServices<IHostedService>();
        await Task.WhenAll(hostedServices.Select(s => s.StartAsync(cancellationToken)));

        var tcs = new TaskCompletionSource();
        cancellationToken.Register(() => tcs.SetResult());
        await tcs.Task;

        await Task.WhenAll(hostedServices.Select(s => s.StopAsync(CancellationToken.None)));
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
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Connecting to {address}:{port}...", address, port);

        var channel = GrpcChannel.ForAddress($"https://{address}:{port}");
        var client = new TransportService.TransportServiceClient(channel);

        var orchestrator = serviceProvider.GetRequiredService<X3DHOrchestrator>();
        var sessionManager = serviceProvider.GetRequiredService<DirectSessionManager>();
        var activeIdentity = serviceProvider.GetRequiredService<ActiveIdentityContext>();

        // 1. Create the initiator's bundle
        var localKeys = activeIdentity.X3dhKeys ?? throw new InvalidOperationException("X3DH keys not found in active identity.");
        var signedPreKeyBytes = localKeys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
        var localBundle = new PreKeyBundle
        {
            IdentityKey = ByteString.CopyFrom(localKeys.IdentityAgreementKey.PublicKey.ExportSubjectPublicKeyInfo()),
            SignedPreKey = ByteString.CopyFrom(signedPreKeyBytes),
            PreKeySignature = ByteString.CopyFrom(localKeys.IdentitySigningKey.SignData(signedPreKeyBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
            OneTimePreKey = ByteString.CopyFrom(localKeys.OneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo())
        };

        // 2. Send request and get the responder's bundle
        var request = new EstablishSessionRequest { InitiatorBundle = localBundle };
        var response = await client.EstablishSessionAsync(request, cancellationToken: cancellationToken);
        var remotePreKeyBundle = response.ResponderBundle;

        // TODO: The remote PeerId needs to be resolved properly. For now, we generate a new one.
        var remotePeerId = new SessionPeerId(Guid.NewGuid());

        // 3. Use the orchestrator to derive the shared secret
        var initiationResult = orchestrator.InitiateHandshake(remotePeerId, remotePreKeyBundle);

        // 4. Establish the session locally
        var conversationId = await sessionManager.EstablishSessionAsync(remotePeerId, initiationResult.SharedSecret, initiationResult.InitialRatchetPublicKey);

        logger.LogInformation("Session established with peer. Conversation ID: {conversationId}", conversationId);
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
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        var sessionManager = serviceProvider.GetRequiredService<DirectSessionManager>();

        logger.LogInformation("Sending message to conversation {conversationId}...", conversationId);

        var encryptedMessage = await sessionManager.SendMessageAsync(conversationId, message);

        logger.LogInformation("Message sent. Encrypted size: {size} bytes", encryptedMessage.Ciphertext.Length);
    });
    rootCommand.AddCommand(sendCommand);

    return rootCommand;
}
