using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application;
using Percolator.Application.Handlers;
using Percolator.Identity;
using Percolator.Network;
using System.CommandLine;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;

// 1. DEFINE COMMAND-LINE INTERFACE
var portOption = new Option<int>(
    name: "--port",
    description: "The port to listen on for gRPC services.",
    getDefaultValue: () => 9000);

var filePathArgument = new Argument<FileInfo>(
    name: "file-path",
    description: "The full path to the file.");

var addFileCommand = new Command("add-file", "Create a manifest for a file and share it.")
{
    filePathArgument
};

var rootCommand = new RootCommand("Percolator Node")
{
    portOption,
    addFileCommand
};

// 2. SET UP COMMAND HANDLERS
rootCommand.SetHandler(RunNodeAsync, portOption);
addFileCommand.SetHandler(AddFile, filePathArgument);

// 3. INVOKE THE APPROPRIATE HANDLER
return await rootCommand.InvokeAsync(args);

// --- HANDLER IMPLEMENTATIONS ---

async Task RunNodeAsync(int port)
{
    var builder = WebApplication.CreateBuilder(args);

    // Load or generate certificate and configure Kestrel with HTTPS
    var percolatorAppDataPath = GetAndCreatePercolatorAppDataPath();
    var certPath = Path.Combine(percolatorAppDataPath, "node.pfx");
    var selfSignedCert = LoadOrGenerateCertificate(certPath);

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Any, port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(selfSignedCert);
        });
        options.Listen(IPAddress.Loopback, port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(selfSignedCert);
        });
    });

    // Configure services for Dependency Injection
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<AnnounceManifestsOnPeerDiscoveredHandler>();
    });

    builder.Services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();
    builder.Services.AddSingleton<PeerConnectionManager>();
    builder.Services.AddSingleton(sp => new PeerDiscoveryService(
        port,
        selfSignedCert.Thumbprint,
        sp.GetRequiredService<IPeerDiscoveryHandler>()
    ));
    builder.Services.AddSingleton<ManifestStore>();
    builder.Services.AddSingleton<ManifestService>();
    builder.Services.AddSingleton<IIdentityService, IdentityService>();

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<FileSharingService>();

    var app = builder.Build();

    // Configure the HTTP request pipeline
    app.MapGrpcService<FileSharingService>();
    app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. Secure connection established.");

    // Set up application lifetime events
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var discoveryService = app.Services.GetRequiredService<PeerDiscoveryService>();
    lifetime.ApplicationStarted.Register(() => discoveryService.StartAsync(CancellationToken.None));
    lifetime.ApplicationStopping.Register(() => discoveryService.Stop());

    await app.RunAsync();
}

void AddFile(FileInfo fileInfo)
{
    if (!fileInfo.Exists)
    {
        Console.WriteLine($"Error: File not found at '{fileInfo.FullName}'");
        return;
    }

    // Use a minimal host for DI to access required services for this one-shot command
    using var host = CreateCliHost();
    var manifestService = host.Services.GetRequiredService<ManifestService>();
    var manifestStore = host.Services.GetRequiredService<ManifestStore>();

    Console.WriteLine($"Creating manifest for {fileInfo.FullName}...");
    var (hash, manifest) = manifestService.CreateManifestFromFile(fileInfo.FullName);
    manifestStore.StoreManifest(hash, manifest);
    Console.WriteLine($"Successfully created and stored manifest with hash: {hash.ToBase64()}");
}

// --- HELPER METHODS ---

static IHost CreateCliHost()
{
    return Host.CreateDefaultBuilder()
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<ManifestStore>();
            services.AddSingleton<IIdentityService, IdentityService>();
            services.AddSingleton<ManifestService>();
        })
        .Build();
}

static string GetAndCreatePercolatorAppDataPath()
{
    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
    Directory.CreateDirectory(percolatorAppDataPath);
    return percolatorAppDataPath;
}

static X509Certificate2 LoadOrGenerateCertificate(string certPath)
{
    if (File.Exists(certPath))
    {
        Console.WriteLine($"[Security] Loading existing certificate from: {certPath}");
        var certBytes = File.ReadAllBytes(certPath);
        return X509CertificateLoader.LoadPkcs12(certBytes, password: null);
    }
    else
    {
        Console.WriteLine("[Security] No existing certificate found. Generating a new one.");
        var selfSignedCert = CertificateGenerator.CreateSelfSignedCertificate();
        Console.WriteLine($"[Security] Saving new certificate to: {certPath}");
        var certBytes = selfSignedCert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(certPath, certBytes);
        return selfSignedCert;
    }
}
