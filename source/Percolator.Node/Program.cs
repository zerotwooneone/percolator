using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Infrastructure;
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
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var port = context.ParseResult.GetValueForOption(portOption);
    var cancellationToken = context.GetCancellationToken();

    // Step 1: Create a temporary, but complete, service provider to resolve the TLS certificate.
    // This breaks the circular dependency between Kestrel configuration and service initialization.
    var tempServices = new ServiceCollection();
    tempServices.AddLogging(b => b.AddConsole());
    tempServices.AddApplicationServices(new ConfigurationBuilder().Build());
    tempServices.AddInfrastructureServices(new ConfigurationBuilder().Build());
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
    builder.Services.AddInfrastructureServices(builder.Configuration);
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
    var configuration = new ConfigurationBuilder().Build();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    services.AddInfrastructureServices(configuration);

    await using var serviceProvider = services.BuildServiceProvider();

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var conversationService = serviceProvider.GetRequiredService<IConversationService>();

    Console.WriteLine($"Connecting to {host}:{port}...");
    try
    {
        var conversationId = await conversationService.CreateDirectConversationAsync(host, port);
        Console.WriteLine($"Session established. Conversation ID: {conversationId}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to establish session: {ex.Message}");
        Console.ResetColor();
    }
});

sendCommand.SetHandler(async (InvocationContext context) =>
{
    var conversationIdGuid = context.ParseResult.GetValueForArgument(conversationIdArgument);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var services = new ServiceCollection();
    var configuration = new ConfigurationBuilder().Build();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    services.AddInfrastructureServices(configuration);

    await using var serviceProvider = services.BuildServiceProvider();

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var messageService = serviceProvider.GetRequiredService<IMessageService>();
    var conversationId = new ChatConversationId(conversationIdGuid);

    Console.WriteLine($"Sending message to conversation {conversationId}...");
    try
    {
        var sentMessage = await messageService.SendDirectMessageAsync(conversationId, message);
        Console.WriteLine($"Message sent with ID: {sentMessage.Id}");
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Failed to send message: {ex.Message}");
        Console.ResetColor();
    }
});

// --- Run Application ---
return await rootCommand.InvokeAsync(args);
