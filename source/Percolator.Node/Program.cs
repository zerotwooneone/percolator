using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application;
using Percolator.Network;
using System.Net;
using System.CommandLine;
using Percolator.Node;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Percolator.Application;

// Set up command-line parsing
var portOption = new Option<int>(
    name: "--port",
    description: "The port to listen on for gRPC services.",
    getDefaultValue: () => 9000);

var rootCommand = new RootCommand("Percolator Node");
rootCommand.AddOption(portOption);

// The handler is passed the parsed value of the option.
rootCommand.SetHandler(async (port) =>
{
    // Use WebApplication.CreateBuilder for a combined app/web host.
    // Pass an empty string array to avoid conflicts with System.CommandLine parsing.
    var builder = WebApplication.CreateBuilder(new string[0]);

    // 1. Load or generate certificate and configure Kestrel with HTTPS
    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
    var certPath = Path.Combine(percolatorAppDataPath, "node.pfx");

    Directory.CreateDirectory(percolatorAppDataPath);

    X509Certificate2 selfSignedCert;
    if (File.Exists(certPath))
    {
        Console.WriteLine($"[Security] Loading existing certificate from: {certPath}");
        var certBytes = File.ReadAllBytes(certPath);
        selfSignedCert = X509CertificateLoader.LoadPkcs12(certBytes, password: null);
    }
    else
    {
        Console.WriteLine("[Security] No existing certificate found. Generating a new one.");
        selfSignedCert = CertificateGenerator.CreateSelfSignedCertificate();
        Console.WriteLine($"[Security] Saving new certificate to: {certPath}");
        var certBytes = selfSignedCert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(certPath, certBytes);
    }

    Console.WriteLine($"[Security] Using certificate with thumbprint: {selfSignedCert.Thumbprint}");

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

    // 2. Configure services for Dependency Injection
    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<PeerDiscoveryHandler>();
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

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<FileSharingService>();

    // 3. Build the application
    var app = builder.Build();

    // 4. Configure the HTTP request pipeline
    app.MapGrpcService<FileSharingService>();
    app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. Secure connection established.");

    // 5. Configure application lifecycle hooks
    var discoveryService = app.Services.GetRequiredService<PeerDiscoveryService>();
    var appLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    appLifetime.ApplicationStarted.Register(() =>
    {
        // Start the service in a non-blocking way and pass the application stopping token
        _ = discoveryService.StartAsync(appLifetime.ApplicationStopping);
    });
    appLifetime.ApplicationStopping.Register(() => discoveryService.Stop());

    // 6. Run the application
    await app.RunAsync();

}, portOption);

// Invoke the command handler with the process arguments
await rootCommand.InvokeAsync(args);
