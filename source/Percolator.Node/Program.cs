using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Messaging;
using Percolator.Application.Identity;
using Percolator.Application.Security;
using Percolator.Cryptography;
using Percolator.Identity;
using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Percolator.Application.Manifests;
using Percolator.Contracts.Protos;
using Percolator.Application.PeerDiscovery;

var services = ConfigureServices(args);
await using var serviceProvider = services.BuildServiceProvider();

var rootCommand = BuildCommandLine(serviceProvider, services);

await rootCommand.InvokeAsync(args);

static IServiceCollection ConfigureServices(string[] args)
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

    // Domain Services
    

    // Application Service Registration
    services.AddMessaging();
    services.AddIdentity();
    services.AddManifests();
    services.AddPeerDiscovery();
    services.AddAppSecurity();

    services.AddGrpc();

    return services;
}

static RootCommand BuildCommandLine(IServiceProvider serviceProvider, IServiceCollection services)
{
    var portOption = new Option<int>(
        name: "--port",
        description: "The port to listen on for incoming connections.",
        getDefaultValue: () => 5001);

    var identityOption = new Option<string>(
        name: "--identity",
        description: "The name of the identity for the node to use.")
    { IsRequired = true };

    var sendDirectMessageCommand = new Command("send-dm", "Sends a direct message to a peer, initiating a secure session if one doesn't exist.");
    var recipientIdOption = new Option<string>("--recipient-id", "The user ID (certificate thumbprint) of the recipient.") { IsRequired = true };
    var recipientIdentityOption = new Option<string>("--recipient-identity", "The name of the identity to message on the recipient's node.") { IsRequired = true };
    var senderIdentityOption = new Option<string>("--sender-identity", "The name of the identity to send the message from.") { IsRequired = true };
    var peerPortOption = new Option<int>("--peer-port", "The port of the peer to connect to.") { IsRequired = true };
    var messageArgument = new Argument<string>("message", "The content of the message to send.");
    sendDirectMessageCommand.AddOption(recipientIdOption);
    sendDirectMessageCommand.AddOption(recipientIdentityOption);
    sendDirectMessageCommand.AddOption(senderIdentityOption);
    sendDirectMessageCommand.AddOption(peerPortOption);
    sendDirectMessageCommand.AddArgument(messageArgument);

    var createIdentityCommand = new Command("create-identity", "Creates a new user identity.");
    var identityNameArgument = new Argument<string>("name", "The name for the new identity.");
    createIdentityCommand.AddArgument(identityNameArgument);

    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddGlobalOption(portOption);
    rootCommand.AddOption(identityOption); 
    rootCommand.AddCommand(sendDirectMessageCommand);
    rootCommand.AddCommand(createIdentityCommand);

    rootCommand.SetHandler(async (port, identity) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting node on port {Port} for identity '{Identity}'...", port, identity);

        await RunNodeAsync(port, identity, services);

        logger.LogInformation("Node stopped.");
    }, portOption, identityOption);

    sendDirectMessageCommand.SetHandler(async (context) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var port = context.ParseResult.GetValueForOption(peerPortOption);
        var recipientId = context.ParseResult.GetValueForOption(recipientIdOption)!;
        var recipientIdentity = context.ParseResult.GetValueForOption(recipientIdentityOption)!;
        var senderIdentityName = context.ParseResult.GetValueForOption(senderIdentityOption)!;
        var message = context.ParseResult.GetValueForArgument(messageArgument)!;

        logger.LogInformation("Attempting to send direct message to {RecipientId} ({RecipientIdentity}) on port {Port}...", recipientId, recipientIdentity, port);

        try
        {
            var identityService = serviceProvider.GetRequiredService<IIdentityService>();
            var senderCertificate = identityService.GetIdentityCertificate(senderIdentityName);

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddConsole()
                    .AddFilter("System.Net.Http", LogLevel.Trace)
                    .AddFilter("Grpc.Net.Client", LogLevel.Trace);
            });

            var httpHandler = new HttpClientHandler();
            httpHandler.ClientCertificates.Add(senderCertificate);
            httpHandler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
            httpHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var channel = GrpcChannel.ForAddress($"https://localhost:{port}", new GrpcChannelOptions { HttpHandler = httpHandler, LoggerFactory = loggerFactory });
            var client = new Messaging.MessagingClient(channel);

            // 2. Call GetPreKeyBundle
            logger.LogInformation("Requesting pre-key bundle from recipient...");
            var preKeyBundleResponse = await client.GetPreKeyBundleAsync(new GetPreKeyBundleRequest { UserId = recipientId, IdentityName = recipientIdentity });
            var remoteBundleProto = preKeyBundleResponse.Bundle;
            logger.LogInformation("Received pre-key bundle.");

            // 3. Generate ephemeral key
            using var ephemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // 4. Get our own identity keys
            var (localIdentityKey, _, _) = identityService.GetIdentityKeys(senderIdentityName);

            // 5. Call X3DHManager.InitiateHandshake
            logger.LogInformation("Performing X3DH handshake...");
            var cryptoBundle = new Percolator.Cryptography.PreKeyBundle(
                remoteBundleProto.IdentityKey.ToByteArray(),
                remoteBundleProto.SignedPreKey.ToByteArray(),
                remoteBundleProto.OneTimePreKey.ToByteArray(),
                remoteBundleProto.SignedPreKeySignature.ToByteArray());

            var sharedSecret = X3DHManager.InitiateHandshake(cryptoBundle, localIdentityKey, ephemeralKeyPair);
            logger.LogInformation("Handshake successful. Shared secret computed.");

            // 6. Encrypt message
            // TODO: Replace with proper AES-GCM encryption
            var encryptedPayload = System.Text.Encoding.UTF8.GetBytes(message);
            logger.LogWarning("Message is NOT encrypted. Sending plaintext payload.");

            // 7. Call SendPreKeyDirectMessage
            logger.LogInformation("Sending initial message...");
            var sendMessageRequest = new SendPreKeyDirectMessageRequest
            {
                IdentityName = recipientIdentity,
                InitiatorIdentityKey = ByteString.CopyFrom(localIdentityKey.PublicKey.ExportSubjectPublicKeyInfo()),
                InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo()),
                EncryptedPayload = ByteString.CopyFrom(encryptedPayload)
            };

            var sendResponse = await client.SendPreKeyDirectMessageAsync(sendMessageRequest);
            if (sendResponse.Success)
            {
                logger.LogInformation("Successfully sent message and established secure session.");
            }
            else
            {
                logger.LogError("Failed to send message.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while sending the direct message.");
            context.ExitCode = 1;
        }
    });

    createIdentityCommand.SetHandler<string>((name) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Creating new identity '{Name}'...", name);
        var identityService = serviceProvider.GetRequiredService<IIdentityService>();
        try
        {
            var certificate = identityService.CreateIdentity(name);
            logger.LogInformation("Successfully created identity '{Name}'.", name);
            logger.LogInformation("Certificate Thumbprint (User ID): {Thumbprint}", certificate.Thumbprint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create identity.");
        }
    }, identityNameArgument);

    return rootCommand;
}

static async Task RunNodeAsync(int port, string identityName, IServiceCollection initialServices)
{
    var builder = WebApplication.CreateBuilder();

    // Transfer services from the initial collection
    foreach (var service in initialServices)
    {
        builder.Services.Add(service);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Any, port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(https_options =>
            {
                var identityService = listenOptions.ApplicationServices.GetRequiredService<IIdentityService>();
                var certificate = identityService.GetIdentityCertificate(identityName);
                https_options.ServerCertificate = certificate;
                https_options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https_options.ClientCertificateValidation = (cert, chain, errors) =>
                {
                    // TODO: Implement proper certificate validation based on a trust store
                    return true;
                };
            });
        });
    });

    var app = builder.Build();

    app.MapGrpcService<MessagingGrpcService>();
    app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

    await app.RunAsync();
}
