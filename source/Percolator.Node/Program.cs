using Google.Protobuf;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net;
using Percolator.Application;
using Percolator.Contracts.Protos;
using Percolator.Network;
using System.Security.Cryptography;

// 1. Configure the host and services
var builder = WebApplication.CreateBuilder(args);

// Add MediatR and scan the Application assembly for handlers
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PeerDiscoveryHandler>());

var grpcPort = builder.Configuration.GetValue<int>("port", 50051);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
    options.Listen(IPAddress.Loopback, grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddGrpc();

// Register the handler implementation from the Application layer
builder.Services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();

// Register the domain service using a factory to inject its dependencies
builder.Services.AddSingleton(provider =>
{
    var handler = provider.GetRequiredService<IPeerDiscoveryHandler>();
    return new PeerDiscoveryService(grpcPort, handler);
});

builder.Services.AddSingleton<PeerConnectionManager>();
builder.Services.AddSingleton<FileSharingService>();
builder.Services.AddSingleton<ManifestStore>();
builder.Services.AddSingleton<ManifestService>();

var app = builder.Build();

// 2. Configure the HTTP pipeline
app.MapGrpcService<FileSharingService>();
app.MapGet("/", () => "Percolator Node is running.");

// 3. Manage service lifecycles and get required services for the console
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var discoveryService = app.Services.GetRequiredService<PeerDiscoveryService>();
var connectionManager = app.Services.GetRequiredService<PeerConnectionManager>();
var manifestService = app.Services.GetRequiredService<ManifestService>();
var manifestStore = app.Services.GetRequiredService<ManifestStore>();

lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine("[Kestrel] Server is listening on the following addresses:");
    foreach (var address in app.Urls)
    {
        Console.WriteLine($"- {address}");
    }
    // Start discovery only after the server is ready
    discoveryService.StartAsync(CancellationToken.None).ContinueWith(t =>
    {
        Console.WriteLine("[Discovery] Service failed to start.");
    }, TaskContinuationOptions.OnlyOnFaulted);
});

lifetime.ApplicationStopping.Register(() =>
{
    discoveryService.Stop();
});

// NOTE: The event handler logic has been moved to MediatR notification handlers
// in the Percolator.Application.Handlers namespace, achieving better separation of concerns.

// 4. Run the application and the interactive console
var appTask = app.RunAsync();
Console.WriteLine("Node is running. Type 'peers' to see discovered peers or 'exit' to quit.");

while (true)
{
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    var parts = input.Split(' ', 2);
    var command = parts[0].ToLower();

    switch (command)
    {
        case "exit":
            goto EndOfLoop;

        case "peers":
            Console.WriteLine("Discovered peers:");
            foreach (var peer in discoveryService.DiscoveredPeers)
            {
                Console.WriteLine($"- {peer.IpAddress}:{peer.GrpcEndpoint.Port}");
            }
            break;

        case "manifest" when parts.Length > 1 && parts[1].StartsWith("create "):
            var filePath = parts[1].Substring("create ".Length).Trim();
            if (string.IsNullOrEmpty(filePath))
            {
                Console.WriteLine("Usage: manifest create <file_path>");
                break;
            }

            try
            {
                var (hash, manifest) = manifestService.CreateManifestFromFile(filePath);
                manifestStore.StoreManifest(hash, manifest);
                Console.WriteLine($"Manifest created and stored with hash: {BitConverter.ToString(hash.ToByteArray()).Replace("-", "").Substring(0, 12)}...");

                // Announce to all known peers
                var announcement = new AnnounceManifestRequest { ManifestHash = hash };
                foreach (var peer in discoveryService.DiscoveredPeers)
                {
                    try
                    {
                        var client = connectionManager.GetClient(peer);
                        await client.AnnounceManifestAsync(announcement);
                        Console.WriteLine($"Announced manifest to {peer.IpAddress}:{peer.GrpcEndpoint.Port}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to announce to peer {peer.IpAddress}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating manifest: {ex.Message}");
            }
            break;

        default:
            Console.WriteLine($"Unknown command: {input}");
            break;
    }
}

EndOfLoop:
await app.StopAsync();
await appTask;
