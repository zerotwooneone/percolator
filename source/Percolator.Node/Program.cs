using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Security.Cryptography.X509Certificates;
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
var invitationLinkArgument = new Argument<string>("link", "The percolator:// invitation link from the peer.");
var connectCommand = new Command("connect", "Connects to a peer using an invitation link.")
{
    invitationLinkArgument,
    identityOption // Add identity option to client commands
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var conversationIdOption = new Option<Guid?>("--conversation-id", "The ID of the conversation. If omitted, the last active conversation with the target peer will be used.");
var peerIdOption = new Option<Guid?>("--peer-id", "The ID of the peer to send the message to. Required if conversation-id is not specified.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var inviteOption = new Option<string>("--invite", "The invitation link to connect and send in one step.");

var sendCommand = new Command("send", "Sends an encrypted message to a peer over an established session.")
{
    conversationIdOption,
    peerIdOption,
    messageArgument,
    inviteOption,
    identityOption // Add identity option to client commands
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
    IConfigurationRoot tempConfig = new ConfigurationBuilder().Build();
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

        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificateSelector = (connectionContext, name) => serverCertificate;
                });
            });
        });

        builder.Logging.ClearProviders().AddConsole();
        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddInfrastructureServices(builder.Configuration);
        builder.Services.AddGrpc();

        WebApplication app = builder.Build();

        // Step 4: Manually initialize the identity *again* using the main service provider
        // to ensure the ActiveIdentityContext is correct for the running application.
        IIdentityOrchestrator identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        // Step 5: Configure and run the application.
        app.MapGrpcService<PercolatorMessageService>();
        await app.RunAsync(cancellationToken);

    }
    finally
    {
        await tempServiceProvider.DisposeAsync();
    }
}

async Task ConnectCommandHandler(InvocationContext context)
{
    var link = context.ParseResult.GetValueForArgument(invitationLinkArgument);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).Build();
    await using var serviceProvider = BuildServiceProvider(configuration);

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var conversationService = serviceProvider.GetRequiredService<IConversationService>();

    try
    {
        var invitation = InvitationLink.Parse(link);
        Console.WriteLine($"Connecting to {invitation.Host}:{invitation.Port}...");
        var conversationId = await conversationService.CreateDirectConversationAsync(invitation.Host, invitation.Port, invitation.PublicKey);
        Console.WriteLine($"Session established. Conversation ID: {conversationId}");
    }
    catch (FormatException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid invitation link: {ex.Message}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An unexpected error occurred: {ex.Message}");
        Console.ResetColor();
    }
}

async Task SendCommandHandler(InvocationContext context)
{
    var conversationIdGuid = context.ParseResult.GetValueForOption(conversationIdOption);
    var peerIdGuid = context.ParseResult.GetValueForOption(peerIdOption);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var inviteLink = context.ParseResult.GetValueForOption(inviteOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);

    // Build client-specific service provider
    var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).Build();
    await using var serviceProvider = BuildServiceProvider(configuration);

    // Initialize identity
    var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
    await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, context.GetCancellationToken());

    var messageService = serviceProvider.GetRequiredService<IMessageService>();
    var conversationService = serviceProvider.GetRequiredService<IConversationService>();

    if (inviteLink != null)
    {
        try
        {
            var invitation = InvitationLink.Parse(inviteLink);
            Console.WriteLine($"Connecting to {invitation.Host}:{invitation.Port}...");
            var conversationId = await conversationService.CreateDirectConversationAsync(invitation.Host, invitation.Port, invitation.PublicKey);
            Console.WriteLine($"Session established. Conversation ID: {conversationId}");
            var sentMessage = await messageService.SendDirectMessageAsync(conversationId, message);
            Console.WriteLine($"Message sent with ID: {sentMessage.Id}");
        }
        catch (FormatException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Invalid invitation link: {ex.Message}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to send message: {ex.Message}");
            Console.ResetColor();
        }
    }
    else
    {
        if (conversationIdGuid.HasValue)
        {
            Console.WriteLine($"Sending message to conversation {conversationIdGuid}...");
            try
            {
                var sentMessage = await messageService.SendDirectMessageAsync(new ChatConversationId(conversationIdGuid.Value), message);
                Console.WriteLine($"Message sent with ID: {sentMessage.Id}");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to send message: {ex.Message}");
                Console.ResetColor();
            }
        }
        else if (peerIdGuid.HasValue)
        {
            Console.WriteLine($"Sending message to peer {peerIdGuid}...");
            try
            {
                var conversationId = await conversationService.GetLastActiveConversationIdAsync(peerIdGuid.Value);
                if (conversationId.HasValue)
                {
                    var sentMessage = await messageService.SendDirectMessageAsync(new ChatConversationId(conversationId.Value.Value), message);
                    Console.WriteLine($"Message sent with ID: {sentMessage.Id}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"No active conversation found with peer {peerIdGuid}.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to send message: {ex.Message}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Either conversation-id or peer-id must be specified.");
            Console.ResetColor();
        }
    }
}

static ServiceProvider BuildServiceProvider(IConfiguration configuration)
{
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    services.AddInfrastructureServices(configuration);

    return services.BuildServiceProvider();
}
