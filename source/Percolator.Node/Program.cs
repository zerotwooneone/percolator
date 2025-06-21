using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Percolator.Application;
using Percolator.Network;

Console.WriteLine("Starting Percolator Node...");

const int grpcPort = 50051;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to listen on our gRPC port.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(grpcPort);
});

// Add services to the container.
builder.Services.AddGrpc();
builder.Services.AddSingleton<PeerDiscoveryService>(new PeerDiscoveryService(grpcPort));
builder.Services.AddSingleton<FileSharingService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapGrpcService<FileSharingService>();
app.MapGet("/", () => "Percolator Node is running. Press Ctrl+C to shut down.");

// Start the peer discovery service in the background when the application starts.
var discoveryService = app.Services.GetRequiredService<PeerDiscoveryService>();
_ = discoveryService.StartAsync(app.Lifetime.ApplicationStopping);

Console.WriteLine($"gRPC server listening on http://localhost:{grpcPort}");
Console.WriteLine($"Peer discovery started. Broadcasting for gRPC on port {grpcPort}.");

await app.RunAsync();

Console.WriteLine("Node stopped.");
