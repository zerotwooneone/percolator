using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application;
using Percolator.Application.Manifests;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.RateLimiting;
using Percolator.Application.Messaging;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Application.Identity;
using Percolator.Application.Security;
using Percolator.Messaging;
using Percolator.Network;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var serviceProvider = ConfigureServices(args).BuildServiceProvider();

var rootCommand = CreateRootCommand(serviceProvider);

var builder = new CommandLineBuilder(rootCommand);
builder.UseHelp();
builder.UseParseDirective();
builder.UseSuggestDirective();
builder.RegisterWithDotnetSuggest();
builder.UseParseErrorReporting();
builder.UseExceptionHandler();

return await rootCommand.InvokeAsync(args);

static RootCommand CreateRootCommand(IServiceProvider serviceProvider)
{
    var portOption = new Option<int>(
        aliases: new[] { "--port", "-p" },
        description: "The port for the gRPC server to listen on.");
    portOption.SetDefaultValue(9000);

    var requestManifestCommand = new Command("request-manifest", "Requests a manifest from a peer.");
    var hashArgument = new Argument<string>("manifest-hash", "The Base64 hash of the manifest to request.");
    var subPathOption = new Option<string>("--sub-path", "The sub-path within the manifest to request.");
    var peerPortOption = new Option<int>("--port", "The port of the peer to connect to.");
    peerPortOption.SetDefaultValue(9000);
    requestManifestCommand.AddArgument(hashArgument);
    requestManifestCommand.AddOption(subPathOption);
    requestManifestCommand.AddOption(peerPortOption);

    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddGlobalOption(portOption);
    rootCommand.AddCommand(requestManifestCommand);

    rootCommand.SetHandler<int>(async (port) =>
    {
        await RunNodeAsync(port, serviceProvider);
    }, portOption);

    requestManifestCommand.SetHandler(async (InvocationContext context) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var port = context.ParseResult.GetValueForOption(peerPortOption);
        var manifestHash = context.ParseResult.GetValueForArgument(hashArgument);
        var subPath = context.ParseResult.GetValueForOption(subPathOption);

        logger.LogInformation("Requesting sub-manifest for path '{SubPath}' from manifest '{ManifestHash}' on port {Port}...", subPath, manifestHash, port);
        
        var httpHandler = new HttpClientHandler();
        // The client must also validate the server's certificate.
        // We replace the dangerous "accept all" with a check that the server's certificate thumbprint
        // matches our own identity, establishing a self-trust model.
        httpHandler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
        {
            if (cert is null)
            {
                return false;
            }

            // We expect chain errors because we are using self-signed certificates.
            if (errors != SslPolicyErrors.None && errors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                return false;
            }

            var trustedStore = serviceProvider.GetRequiredService<ITrustedPeerStore>();
            return trustedStore.IsTrusted(cert.Thumbprint);
        };

        using var channel = GrpcChannel.ForAddress($"https://localhost:{port}", new GrpcChannelOptions { HttpHandler = httpHandler });
        var client = new FileSharing.FileSharingClient(channel);

        try
        {
            var request = new RequestManifestRequest
            {
                ManifestHash = ByteString.FromBase64(manifestHash!),
                SubPath = subPath ?? string.Empty
            };
            var response = await client.RequestManifestAsync(request);

            if (response.SignedManifest is not null)
            {
                logger.LogInformation("Successfully received manifest.");
            }
            else
            {
                logger.LogWarning("Peer did not return the requested manifest.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while requesting the manifest.");
            context.ExitCode = 1;
        }
    });

    return rootCommand;
}

static async Task RunNodeAsync(int port, IServiceProvider serviceProvider)
{
    var builder = WebApplication.CreateBuilder();

    // Transfer services from the command-line DI container to the web host
    foreach (var service in serviceProvider.GetRequiredService<IServiceCollection>())
    {
        builder.Services.Add(service);
    }

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Any, port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(https =>
            {
                // Select the server certificate
                https.ServerCertificateSelector = (connectionContext, hostName) =>
                {
                    return serviceProvider.GetService<IIdentityService>()?.GetDefaultIdentityCertificate();
                };

                // Require the client to provide a certificate
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;

                // Implement custom validation for the client certificate
                https.ClientCertificateValidation = (certificate, chain, sslPolicyErrors) =>
                {
                    // We expect chain errors because we are using self-signed certificates.
                    if (sslPolicyErrors != SslPolicyErrors.None && sslPolicyErrors != SslPolicyErrors.RemoteCertificateChainErrors)
                    {
                        return false;
                    }

                    // Trust the peer if its certificate thumbprint is in our trusted store.
                    var trustedStore = serviceProvider.GetRequiredService<ITrustedPeerStore>();
                    return trustedStore.IsTrusted(certificate.Thumbprint);
                };
            });
        });
    });

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Starting Percolator node...");
    logger.LogInformation("gRPC server listening on port {Port}", port);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var discoveryService = app.Services.GetRequiredService<IPeerDiscoveryService>();
    lifetime.ApplicationStarted.Register(() =>
    {
        discoveryService.Start();

        // Create and announce manifests for shared directories
        using var scope = app.Services.CreateScope();
        var manifestService = scope.ServiceProvider.GetRequiredService<IManifestService>();
        var manifests = manifestService.CreateManifestsFromSharedDirectories();

        // TODO: Announce these manifests to the network
    });
    lifetime.ApplicationStopping.Register(() => discoveryService.Stop());

    app.MapGrpcService<FileSharingService>();
    app.MapGrpcService<MessagingGrpcService>();

    // Run the host
    await app.RunAsync();
}

static IServiceCollection ConfigureServices(string[] args)
{
    var services = new ServiceCollection();
    services.AddLogging(configure => configure.AddConsole());
    services.AddGrpc(options =>
    {
        // Mitigate DoS attacks by limiting the max message size.
        options.MaxReceiveMessageSize = 4 * 1024 * 1024; // 4 MB
    });

    // Domain Services
    services.AddSingleton<ICredentialService, CredentialService>();
    services.AddSingleton<ICertificateOperations, CertificateOperations>();
    services.AddSingleton<IIdentityService, PersistentIdentityService>();
    services.AddSingleton<IPeerIdentityStore, InMemoryPeerIdentityStore>();
    services.AddSingleton<ITrustedPeerStore, InMemoryTrustedPeerStore>();
    services.AddSingleton<IDiscoverySignatureProvider, DiscoverySignatureProvider>();
    services.AddSingleton<ISharedDirectoryProvider, SharedDirectoryProvider>();
    services.AddSingleton<IRateLimiter, InMemoryRateLimiter>();

    // Application Services
    services.AddSingleton<ManifestStore>();
    services.AddSingleton<IManifestService, ManifestService>();
    services.AddSingleton<IPeerConnectionManager, PeerConnectionManager>();
    services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();
    services.AddSingleton<IPeerDiscoveryService>(sp =>
    {
        var identityService = sp.GetRequiredService<IIdentityService>();
        var thumbprint = identityService.GetDefaultIdentityCertificate().Thumbprint;
        return new PeerDiscoveryService(
            9000,
            thumbprint,
            sp.GetRequiredService<IPeerDiscoveryHandler>(),
            sp.GetRequiredService<IDiscoverySignatureProvider>(),
            sp.GetRequiredService<ILogger<PeerDiscoveryService>>());
    });

    services.AddSingleton<IMessageStore, InMemoryMessageStore>();
    services.AddScoped<IGroupService, GroupService>();
    services.AddScoped<IMessageService, MessageService>();

    services.AddSingleton(services);
    return services;
}
