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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Percolator.Contracts.Protos;
using Google.Protobuf;
using Grpc.Core;

// --- CONSTANTS ---

// The hardcoded password has been removed and is now managed by CredentialService.

// --- ENTRYPOINT & COMMAND SETUP ---

return await Main(args);

async Task<int> Main(string[] args)
{
    // 1. DEFINE COMMAND-LINE INTERFACE
    var portOption = new Option<int>("--port", () => 9000, "Port to run the gRPC server on");
    var filePathArgument = new Argument<FileInfo>("file-path", "The path to the file or directory.");
    var addFileCommand = new Command("add-file", "Creates and stores a manifest for a given file or directory.");
    addFileCommand.AddArgument(filePathArgument);
    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddOption(portOption);
    rootCommand.AddCommand(addFileCommand);

    // Add a temporary command to test requesting manifests
    var requestManifestCommand = new Command("request-manifest", "Requests a manifest or sub-manifest from a peer.");
    var manifestHashArgument = new Argument<string>("manifest-hash", "The Base64 hash of the root manifest.");
    var subPathArgument = new Argument<string>("sub-path", "The relative sub-path to request a manifest for.");
    var peerPortOption = new Option<int>("--port", () => 9000, "The port of the peer to connect to.");
    requestManifestCommand.AddArgument(manifestHashArgument);
    requestManifestCommand.AddArgument(subPathArgument);
    requestManifestCommand.AddOption(peerPortOption);
    rootCommand.AddCommand(requestManifestCommand);

    // --- HANDLER SETUP ---
    rootCommand.SetHandler(async (port) =>
    {
        await RunNodeAsync(port);
    }, portOption);

    addFileCommand.SetHandler((fileInfo) =>
    {
        using var host = CreateCliHost();
        AddFile(fileInfo, host);
    }, filePathArgument);

    requestManifestCommand.SetHandler(RequestManifest, manifestHashArgument, subPathArgument, peerPortOption);

    // --- RUN THE APP ---
    return await rootCommand.InvokeAsync(args);
}

// --- COMMAND HANDLERS ---

async Task RunNodeAsync(int port)
{
    var builder = WebApplication.CreateBuilder(args);

    // Load or generate certificate and configure Kestrel
    var credentialService = new CredentialService();
    var pfxPassword = credentialService.GetOrCreatePfxPassword();
    var percolatorAppDataPath = GetAndCreatePercolatorAppDataPath();
    var certPath = Path.Combine(percolatorAppDataPath, "node.pfx");
    var selfSignedCert = LoadOrGenerateCertificate(certPath, pfxPassword);

    builder.WebHost.ConfigureKestrel(options =>
    {
        // For local testing, we allow insecure HTTP/2. 
        // In a production environment, you would want to enforce HTTPS.
        options.Listen(IPAddress.Any, port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
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
    builder.Services.AddSingleton<IIdentityService, PersistentIdentityService>();
    builder.Services.AddSingleton<ICredentialService>(credentialService);

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

static void AddFile(FileInfo fileInfo, IHost host)
{
    // The FileInfo.Exists property returns false for directories.
    // We must check for both file and directory existence explicitly.
    if (!File.Exists(fileInfo.FullName) && !Directory.Exists(fileInfo.FullName))
    {
        Console.WriteLine($"Error: File or directory not found at '{fileInfo.FullName}'");
        return;
    }

    var manifestService = host.Services.GetRequiredService<ManifestService>();
    var manifestStore = host.Services.GetRequiredService<ManifestStore>();

    var absolutePath = Path.GetFullPath(fileInfo.FullName);
    Console.WriteLine($"Creating manifest for {absolutePath}...");
    var (hash, manifest) = manifestService.CreateManifestFromFile(absolutePath);
    manifestStore.StoreManifest(hash, manifest, absolutePath);
    Console.WriteLine($"Successfully created and stored manifest with hash: {hash.ToBase64()}");
}

static async Task RequestManifest(string manifestHash, string subPath, int port)
{
    Console.WriteLine($"Requesting sub-manifest for path '{subPath}' from manifest '{manifestHash}' on port {port}...");
    var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
    var client = new FileSharing.FileSharingClient(channel);

    var request = new RequestManifestRequest
    {
        ManifestHash = ByteString.FromBase64(manifestHash.Replace('_', '/')), // Handle filename-safe base64
        SubPath = subPath
    };

    try
    {
        var response = await client.RequestManifestAsync(request);
        if (response.SignedManifest != null)
        {
            Console.WriteLine("Successfully received manifest:");
            Console.WriteLine(response.SignedManifest.ToString());
        }
        else
        {
            Console.WriteLine("Failed to retrieve manifest. The peer may not have it or the sub-path may be invalid.");
        }
    }
    catch (RpcException ex)
    {
        Console.WriteLine($"gRPC Error: {ex.StatusCode} - {ex.Status.Detail}");
    }
}

// --- HELPER METHODS ---

static IHost CreateCliHost()
{
    return Host.CreateDefaultBuilder()
        .ConfigureServices((_, services) =>
        {
            services.AddSingleton<ManifestStore>();
            services.AddSingleton<IIdentityService, PersistentIdentityService>();
            services.AddSingleton<ManifestService>();
            services.AddSingleton<ICredentialService, CredentialService>();
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

static X509Certificate2 LoadOrGenerateCertificate(string certPath, string password)
{
    if (File.Exists(certPath))
    {
        try
        {
            var pfxBytes = File.ReadAllBytes(certPath);
            return X509CertificateLoader.LoadPkcs12(pfxBytes, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            Console.WriteLine($"Error loading certificate from {certPath}. It might be corrupted or the password has changed. Error: {ex.Message}");
            Console.WriteLine("Consider deleting the file and letting the application regenerate it.");
            throw;
        }
    }

    Console.WriteLine("Generating new self-signed certificate for the node...");
    var newCert = CertificateGenerator.CreateSelfSignedCertificate("localhost");
    var newPfxBytes = newCert.Export(X509ContentType.Pfx, password);
    File.WriteAllBytes(certPath, newPfxBytes);
    return X509CertificateLoader.LoadPkcs12(newPfxBytes, password, X509KeyStorageFlags.Exportable);
}
