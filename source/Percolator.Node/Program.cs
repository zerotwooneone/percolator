using Google.Protobuf;
using Grpc.Net.Client;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application;
using Percolator.Application.Handlers;
using Percolator.Contracts;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Network;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.IO;
using System.Net;

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

    var addFileCommand = new Command("add-file", "Creates a manifest for a file or directory.");
    var fileArgument = new Argument<FileInfo>("path", "The path to the file or directory.");
    addFileCommand.AddArgument(fileArgument);

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
    rootCommand.AddCommand(addFileCommand);
    rootCommand.AddCommand(requestManifestCommand);

    rootCommand.SetHandler<int>(async (port) =>
    {
        await RunNodeAsync(port, serviceProvider);
    }, portOption);

    addFileCommand.SetHandler((InvocationContext context) =>
    {
        var fileInfo = context.ParseResult.GetValueForArgument(fileArgument);
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var manifestService = serviceProvider.GetRequiredService<IManifestService>();
        var identityService = serviceProvider.GetRequiredService<IIdentityService>();

        if (fileInfo is null || !fileInfo.Exists)
        {
            logger.LogError("Error: File or directory not found at '{FullPath}'", fileInfo?.FullName);
            context.ExitCode = 1;
            return;
        }

        identityService.GetDefaultIdentityCertificate();
        logger.LogInformation("Creating manifest for {AbsolutePath}...", fileInfo.FullName);
        var (hash, _) = manifestService.CreateManifestFromFile(fileInfo.FullName);
        logger.LogInformation("Successfully created and stored manifest with hash: {ManifestHash}", hash.ToBase64());
    });

    requestManifestCommand.SetHandler(async (InvocationContext context) =>
    {
        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
        var port = context.ParseResult.GetValueForOption(peerPortOption);
        var manifestHash = context.ParseResult.GetValueForArgument(hashArgument);
        var subPath = context.ParseResult.GetValueForOption(subPathOption);

        logger.LogInformation("Requesting sub-manifest for path '{SubPath}' from manifest '{ManifestHash}' on port {Port}...", subPath, manifestHash, port);
        using var channel = GrpcChannel.ForAddress($"http://localhost:{port}");
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
                https.ServerCertificateSelector = (connectionContext, hostName) =>
                {
                    return serviceProvider.GetService<IIdentityService>()?.GetDefaultIdentityCertificate();
                };
            });
        });
        options.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http2);
    });

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Starting Percolator node...");
    logger.LogInformation("gRPC server listening on port {Port}", port);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var discoveryService = app.Services.GetRequiredService<IPeerDiscoveryService>();
    lifetime.ApplicationStarted.Register(() => discoveryService.Start());
    lifetime.ApplicationStopping.Register(() => discoveryService.Stop());

    app.MapGrpcService<FileSharingService>();

    await app.RunAsync();
}

static IServiceCollection ConfigureServices(string[] args)
{
    var services = new ServiceCollection();
    services.AddLogging(configure => configure.AddConsole());
    services.AddSingleton<ICredentialService, CredentialService>();
    services.AddSingleton<IIdentityService, PersistentIdentityService>();
    services.AddSingleton<IManifestService, ManifestService>();
    services.AddSingleton<IManifestStore, ManifestStore>();
    services.AddSingleton<IPeerConnectionManager, PeerConnectionManager>();
    services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();
    services.AddSingleton<ISignatureService, SignatureService>();

    // Pre-parse the port to configure services correctly.
    var portOption = new Option<int>(new[] { "--port", "-p" }, () => 9000);
    var preParseCommand = new RootCommand { portOption };
    var port = preParseCommand.Parse(args).GetValueForOption(portOption);

    services.AddSingleton<IPeerDiscoveryService>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<PeerDiscoveryService>>();
        var identity = sp.GetRequiredService<IIdentityService>();
        var handler = sp.GetRequiredService<IPeerDiscoveryHandler>();
        var certificate = identity.GetDefaultIdentityCertificate();
        return new PeerDiscoveryService(port, certificate.Thumbprint, handler, logger);
    });
    services.AddGrpc();
    services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<AnnounceManifestsOnPeerDiscoveredHandler>());

    // Add a reference to the service collection itself so we can transfer it to the WebApplication host.
    services.AddSingleton<IServiceCollection>(services);

    return services;
}
