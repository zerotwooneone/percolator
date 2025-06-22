using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Manifests;
using Percolator.Application.Messaging;
using Percolator.Application.Network;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.Security;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using System.CommandLine;
using System.CommandLine.Invocation;
using Percolator.Network; // Added for PeerDiscoveryService
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

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

    // Application Service Registration
    services.AddCryptography();
    services.AddIdentityServices();
    services.AddMessaging();
    services.AddManifests();
    services.AddNetworkServices();
    services.AddPeerDiscovery();
    services.AddAppSecurity();

    services.AddGrpc();

    return services;
}

static RootCommand BuildCommandLine(IServiceProvider serviceProvider, IServiceCollection services)
{
    var portOption = new Option<int>(
        name: "--port",
        description: "The port to listen on.",
        getDefaultValue: () => 5000);

    var identityOption = new Option<string>(
        name: "--identity",
        description: "The name of the identity to use.",
        getDefaultValue: () => "default");

    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddGlobalOption(portOption);
    rootCommand.AddGlobalOption(identityOption);

    var sendDirectMessageCommand = new Command("send-dm", "Send a direct message to a peer");
    var peerPortOption = new Option<int>("--peer-port", "The port of the recipient peer.") { IsRequired = true };
    var recipientIdOption = new Option<string>("--recipient-id", "The ID of the recipient.") { IsRequired = true };
    var recipientIdentityOption = new Option<string>("--recipient-identity", "The identity name of the recipient.") { IsRequired = true };
    var senderIdentityOption = new Option<string>("--sender-identity", "The identity name of the sender.") { IsRequired = true };
    var messageOption = new Option<string>("--message", "The message to send.") { IsRequired = true };

    sendDirectMessageCommand.AddOption(peerPortOption);
    sendDirectMessageCommand.AddOption(recipientIdOption);
    sendDirectMessageCommand.AddOption(recipientIdentityOption);
    sendDirectMessageCommand.AddOption(senderIdentityOption);
    sendDirectMessageCommand.AddOption(messageOption);

    rootCommand.AddCommand(sendDirectMessageCommand);

    var createIdentityCommand = new Command("create-identity", "Create a new identity");
    var nameOption = new Option<string>("--name", "The name of the identity to create.") { IsRequired = true };
    createIdentityCommand.AddOption(nameOption);
    rootCommand.AddCommand(createIdentityCommand);

    rootCommand.SetHandler(async (port, identity) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting node on port {Port} for identity '{Identity}'...", port, identity);

        await RunNodeAsync(port, identity, services);
    }, portOption, identityOption);

    sendDirectMessageCommand.SetHandler(async (context) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var port = context.ParseResult.GetValueForOption(peerPortOption);
        var recipientId = context.ParseResult.GetValueForOption(recipientIdOption)!;
        var recipientIdentity = context.ParseResult.GetValueForOption(recipientIdentityOption)!;
        var senderIdentityName = context.ParseResult.GetValueForOption(senderIdentityOption)!;
        var message = context.ParseResult.GetValueForOption(messageOption)!;

        logger.LogInformation("Attempting to send direct message to {RecipientId} ({RecipientIdentity}) on port {Port}...", recipientId, recipientIdentity, port);

        try
        {
            var identityService = serviceProvider.GetRequiredService<IIdentityService>();
            var keyManagementService = serviceProvider.GetRequiredService<IKeyManagementService>();
            var x3dhManager = serviceProvider.GetRequiredService<X3DHManager>();

            var senderCertificate = identityService.GetIdentityCertificate(senderIdentityName);

            // Setup gRPC client with mTLS
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(senderCertificate);
            // This is for local dev, allowing self-signed certs. In prod, you'd have a proper CA.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var channel = GrpcChannel.ForAddress($"https://localhost:{port}", new GrpcChannelOptions { HttpHandler = handler });
            var client = new Percolator.Contracts.Protos.Messaging.MessagingClient(channel);

            // 1. Get pre-key bundle from recipient
            logger.LogInformation("Requesting pre-key bundle from peer...");
            var getBundleRequest = new GetPreKeyBundleRequest { IdentityName = recipientIdentity };
            var remoteBundleResponse = await client.GetPreKeyBundleAsync(getBundleRequest);

            // Map from Protobuf bundle to Cryptography domain bundle
            var cryptoBundle = new Percolator.Cryptography.PreKeyBundle(
                remoteBundleResponse.Bundle.IdentityKey.ToByteArray(),
                remoteBundleResponse.Bundle.SignedPreKey.ToByteArray(),
                remoteBundleResponse.Bundle.OneTimePreKey.ToByteArray(),
                remoteBundleResponse.Bundle.SignedPreKeySignature.ToByteArray());

            // 2. Get our own identity keys
            var senderKeys = keyManagementService.GetIdentityKeys(senderIdentityName);

            // 3. Create an ephemeral key pair for this session
            using var ephemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            // 4. Call X3DHManager.InitiateHandshake
            logger.LogInformation("Performing X3DH handshake...");
            var sharedSecret = x3dhManager.InitiateHandshake(
                cryptoBundle,
                senderKeys.IdentitySigningKey,
                senderKeys.IdentityAgreementKey,
                ephemeralKeyPair);
            logger.LogInformation("Handshake complete. Shared secret derived.");

            // 5. Send the initial message with handshake data to establish the session
            logger.LogInformation("Sending pre-key message to establish session...");
            var preKeyMessageRequest = new SendPreKeyDirectMessageRequest
            {
                IdentityName = recipientIdentity,
                InitiatorIdentityKey = ByteString.CopyFrom(senderKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo()),
                // TODO: Actually encrypt the message using the sharedSecret
                EncryptedPayload = ByteString.CopyFrom(System.Text.Encoding.UTF8.GetBytes(message))
            };
            await client.SendPreKeyDirectMessageAsync(preKeyMessageRequest);

            logger.LogInformation("Message sent successfully.");
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
            logger.LogInformation("Successfully created identity '{Name}' with thumbprint {Thumbprint}", name, certificate.Thumbprint);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create identity.");
        }
    }, nameOption);

    return rootCommand;
}

static async Task RunNodeAsync(int port, string identityName, IServiceCollection initialServices)
{
    var builder = WebApplication.CreateBuilder();

    foreach (var service in initialServices)
    {
        builder.Services.Add(service);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            var identityService = builder.Services.BuildServiceProvider().GetRequiredService<IIdentityService>();
            var certificate = identityService.GetIdentityCertificate(identityName);
            //todo: we should create a default identity if one doesn't exist
            listenOptions.UseHttps(certificate);
        });
    });

    var app = builder.Build();

    // Start peer discovery
    var discoveryService = app.Services.GetRequiredService<IPeerDiscoveryService>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

    lifetime.ApplicationStarted.Register(() =>
    {
        // Don't await, let it run in the background
        _ = discoveryService.StartAsync(lifetime.ApplicationStopping);
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        discoveryService.Stop();
    });

    // Configure the HTTP request pipeline.
    app.MapGrpcService<MessagingGrpcService>();
    app.MapGrpcService<FileSharingService>();
    app.MapGrpcService<ManifestService>();

    app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

    await app.RunAsync();
}
