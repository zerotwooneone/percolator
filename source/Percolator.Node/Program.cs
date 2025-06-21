using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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

// Allow the gRPC port to be configured via command-line (e.g., --port 50052)
var grpcPort = builder.Configuration.GetValue<int>("port", 50051);

// Explicitly configure Kestrel to use HTTP/2, which is required for gRPC.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

builder.Services.AddGrpc();

// Register the domain service as a singleton.
builder.Services.AddSingleton(new PeerDiscoveryService(grpcPort));
builder.Services.AddSingleton<PeerConnectionManager>();
builder.Services.AddSingleton<FileSharingService>();

var app = builder.Build();

// Use the application lifetime to manage the discovery service lifecycle.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var discoveryService = app.Services.GetRequiredService<PeerDiscoveryService>();

lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"[Kestrel] Node listening on: {string.Join(", ", app.Urls)}");
    discoveryService.StartAsync(CancellationToken.None).ContinueWith(t =>
    {
        Console.WriteLine("[Discovery] Service failed to start.");
    }, TaskContinuationOptions.OnlyOnFaulted);
});

lifetime.ApplicationStopping.Register(() =>
{
    discoveryService.Stop();
});

// 2. Configure the HTTP pipeline (gRPC server)
app.MapGrpcService<FileSharingService>();
app.MapGet("/", () => "Percolator Node is running. gRPC services are available.");

// 3. Set up application logic and run
var connectionManager = app.Services.GetRequiredService<PeerConnectionManager>();

// PeerDiscovered is an EventHandler<Peer>, so it takes (sender, peer).
discoveryService.PeerDiscovered += async (sender, peer) =>
{
    Console.WriteLine($"+ Peer discovered: {peer.IpAddress}:{peer.GrpcEndpoint.Port}");
    var client = connectionManager.GetClient(peer);
    try
    {
        var manifest = new Manifest
        {
            AuthorIdentityPublicKey = ByteString.Empty,
            TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow)
        };
        var manifestBytes = manifest.ToByteArray();
        var manifestHash = SHA256.HashData(manifestBytes);
        var request = new AnnounceManifestRequest
        {
            ManifestHash = ByteString.CopyFrom(manifestHash)
        };
        var response = await client.AnnounceManifestAsync(request);
        Console.WriteLine($"Sent manifest announcement to {peer.IpAddress}. Ack: {response.Ack}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error communicating with peer {peer.IpAddress}: {ex.Message}");
    }
};

// PeerExpired now uses EventHandler<Peer>, so it takes (sender, peer).
discoveryService.PeerExpired += (sender, peer) =>
{
    Console.WriteLine($"- Peer expired: {peer.IpAddress}:{peer.GrpcEndpoint.Port}");
    connectionManager.RemovePeer(peer);
};

var appTask = app.RunAsync();

Console.WriteLine("Node is running. Type 'peers' to see discovered peers or 'exit' to quit.");

// 4. Interactive console loop
while (true)
{
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (input.Equals("peers", StringComparison.OrdinalIgnoreCase))
    {
        var peers = discoveryService.DiscoveredPeers;
        if (!peers.Any())
        {
            Console.WriteLine("No peers discovered.");
        }
        else
        {
            Console.WriteLine("Discovered peers:");
            foreach (var peer in peers)
            {
                Console.WriteLine($"- {peer.IpAddress}:{peer.GrpcEndpoint.Port} (Last seen: {peer.LastSeenUtc:T})");
            }
        }
    }
    else
    {
        Console.WriteLine($"Unknown command: '{input}'.");
    }
}

// 6. Graceful shutdown
Console.WriteLine("Shutting down...");
await app.StopAsync();

Console.WriteLine("Node stopped.");
