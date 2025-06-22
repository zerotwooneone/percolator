using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Manifests;
using Percolator.Application.Messaging;
using Percolator.Application.Network;
using Percolator.Application.Security;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Percolator.Application.PeerDiscovery;

// --- Main Entry Point ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = ConfigureServices(configuration);
await using var serviceProvider = services.BuildServiceProvider();

var rootCommand = BuildCommandLine(serviceProvider);

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
    services.AddMessaging();
    services.AddManifests();
    services.AddNetworkServices(configuration);
    services.AddPeerDiscovery();
    services.AddAppSecurity();

    services.AddGrpc();

    return services;
}

// --- Command Line Interface Setup ---
static RootCommand BuildCommandLine(IServiceProvider serviceProvider)
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
    runCommand.SetHandler(async (port, identity) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting node on port {Port} for identity '{Identity}'...", port, identity);
        await RunNodeAsync(port, identity, serviceProvider);
    }, portOption, identityOption);

    // --- 'send-dm' Command ---
    var sendDirectMessageCommand = new Command("send-dm", "Send a direct message to a peer.");
    var peerPortOption = new Option<int>("--peer-port", "The port of the recipient peer.") { IsRequired = true };
    var recipientIdentityOption = new Option<string>("--recipient-identity", "The identity name of the recipient.") { IsRequired = true };
    var senderIdentityOption = new Option<string>("--sender-identity", "The identity name of the sender.") { IsRequired = true };
    var messageOption = new Option<string>("--message", "The message to send.") { IsRequired = true };
    sendDirectMessageCommand.AddOption(peerPortOption);
    sendDirectMessageCommand.AddOption(recipientIdentityOption);
    sendDirectMessageCommand.AddOption(senderIdentityOption);
    sendDirectMessageCommand.AddOption(messageOption);
    sendDirectMessageCommand.SetHandler(async (context) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var port = context.ParseResult.GetValueForOption(peerPortOption);
        var recipientIdentity = context.ParseResult.GetValueForOption(recipientIdentityOption)!;
        var senderIdentityName = context.ParseResult.GetValueForOption(senderIdentityOption)!;
        var message = context.ParseResult.GetValueForOption(messageOption)!;

        logger.LogInformation("Attempting to send direct message to {RecipientIdentity} on port {Port}...", recipientIdentity, port);

        try
        {
            var identityService = serviceProvider.GetRequiredService<IIdentityService>();
            var keyManagementService = serviceProvider.GetRequiredService<IKeyManagementService>();
            var x3dhManager = serviceProvider.GetRequiredService<X3DHManager>();

            var senderCertificate = identityService.GetIdentityCertificate(senderIdentityName);

            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(senderCertificate);
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var channel = GrpcChannel.ForAddress($"https://localhost:{port}", new GrpcChannelOptions { HttpHandler = handler });
            var client = new Messaging.MessagingClient(channel);

            logger.LogInformation("Requesting pre-key bundle from peer...");
            var getBundleRequest = new GetPreKeyBundleRequest { IdentityName = recipientIdentity };
            var remoteBundleResponse = await client.GetPreKeyBundleAsync(getBundleRequest);

            var cryptoBundle = new Percolator.Cryptography.PreKeyBundle(
                remoteBundleResponse.Bundle.IdentityKey.ToByteArray(),
                remoteBundleResponse.Bundle.SignedPreKey.ToByteArray(),
                remoteBundleResponse.Bundle.OneTimePreKey.ToByteArray(),
                remoteBundleResponse.Bundle.SignedPreKeySignature.ToByteArray());

            var senderKeys = keyManagementService.GetIdentityKeys(senderIdentityName);
            using var ephemeralKeyPair = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

            logger.LogInformation("Performing X3DH handshake...");
            var sharedSecret = x3dhManager.InitiateHandshake(
                cryptoBundle,
                senderKeys.IdentitySigningKey,
                senderKeys.IdentityAgreementKey,
                ephemeralKeyPair);
            logger.LogInformation("Handshake complete. Shared secret derived.");

            logger.LogInformation("Sending pre-key message to establish session...");
            var preKeyMessageRequest = new SendPreKeyDirectMessageRequest
            {
                IdentityName = recipientIdentity,
                InitiatorIdentityKey = ByteString.CopyFrom(senderKeys.IdentitySigningKey.ExportSubjectPublicKeyInfo()),
                InitiatorEphemeralKey = ByteString.CopyFrom(ephemeralKeyPair.PublicKey.ExportSubjectPublicKeyInfo()),
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

    // --- 'create-identity' Command ---
    var createIdentityCommand = new Command("create-identity", "Create a new identity.");
    var nameOption = new Option<string>("--name", "The name for the new identity.") { IsRequired = true };
    createIdentityCommand.AddOption(nameOption);
    createIdentityCommand.SetHandler<string>((name) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var identityService = serviceProvider.GetRequiredService<IIdentityService>();
        try
        {
            identityService.CreateIdentity(name);
            logger.LogInformation("Identity '{Name}' created successfully.", name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create identity '{Name}'.", name);
        }
    }, nameOption);

    // --- Root Command Setup ---
    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddCommand(runCommand);
    rootCommand.AddCommand(sendDirectMessageCommand);
    rootCommand.AddCommand(createIdentityCommand);

    return rootCommand;
}

// --- Node Host Execution ---
static async Task RunNodeAsync(int port, string identityName, IServiceProvider serviceProvider)
{
    var builder = WebApplication.CreateBuilder();

    // Transfer configuration and services to the WebApplication host
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    builder.Configuration.AddConfiguration(configuration);
    var services = ConfigureServices(builder.Configuration);
    foreach (var service in services)
    {
        builder.Services.Add(service);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            var certProvider = builder.Services.BuildServiceProvider().GetRequiredService<IIdentityService>();
            var certificate = certProvider.GetIdentityCertificate(identityName);
            listenOptions.UseHttps(certificate);
        });
    });

    var app = builder.Build();

    // Start peer discovery using application lifetime hooks
    var discoveryService = app.Services.GetRequiredService<IPeerDiscoveryService>();
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

    lifetime.ApplicationStarted.Register(() =>
    {
        _ = discoveryService.StartAsync(lifetime.ApplicationStopping);
    });

    lifetime.ApplicationStopping.Register(() =>
    {
        discoveryService.Stop();
    });

    // Configure the gRPC pipeline
    app.MapGrpcService<MessagingGrpcService>();
    app.MapGrpcService<FileSharingService>();
    app.MapGrpcService<ManifestService>();

    await app.RunAsync();
}
