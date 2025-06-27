using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Security;
using Percolator.Cryptography;
using Percolator.Identity;
using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.Sessions;
using Percolator.Contracts;
using ServiceCollectionExtensions = Percolator.Application.Sessions.ServiceCollectionExtensions;

// --- Main Entry Point ---
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

var services = ConfigureServices(configuration);
await using var serviceProvider = services.BuildServiceProvider();

var rootCommand = BuildCommandLine(serviceProvider, args);

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
    services.AddSessions();
    services.AddNetworkServices(configuration);
    services.AddPeerDiscovery();
    services.AddAppSecurity();

    services.AddGrpc();

    return services;
}

// --- Command Line Interface Setup ---
static RootCommand BuildCommandLine(IServiceProvider serviceProvider, string[] args)
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
    runCommand.SetHandler(async (identityName, port) =>
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- Service Configuration ---
        builder.Services.AddLogging(configure =>
        {
            configure.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        builder.Services.AddSingleton(builder.Configuration);
        builder.Services.AddCryptography();
        builder.Services.AddIdentityServices();
        builder.Services.AddSessions();
        builder.Services.AddNetworkServices(builder.Configuration);
        builder.Services.AddPeerDiscovery();
        builder.Services.AddAppSecurity();
        builder.Services.AddGrpc();

        // Use the application layer hosted service to manage the discovery service's lifecycle
        builder.Services.AddHostedService<PeerDiscoveryHostedService>();

        var app = builder.Build();

        var identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadActiveIdentityAsync(identityName);

        var activeIdentity = app.Services.GetRequiredService<ActiveIdentityContext>();
        app.Urls.Add($"https://0.0.0.0:{port}");
        await app.RunAsync();

    }, identityOption, portOption);

    var rootCommand = new RootCommand("Percolator Node");
    rootCommand.AddCommand(runCommand);
    rootCommand.AddGlobalOption(identityOption);

    // --- 'create-identity' Command ---
    var nameArgument = new Argument<string>("name", "The name of the identity to create.");
    var nicknameOption = new Option<string>("--nickname", "An optional nickname for the identity.");
    var createIdentityCommand = new Command("create-identity", "Create a new identity.");
    createIdentityCommand.AddArgument(nameArgument);
    createIdentityCommand.AddOption(nicknameOption);
    createIdentityCommand.SetHandler(async (name, nickname) =>
    {
        var identityService = serviceProvider.GetRequiredService<Percolator.Identity.IIdentityService>();
        var identityRecord = await identityService.CreateIdentityAsync(name, nickname);
        Console.WriteLine($"Identity created successfully:\n  Name: {identityRecord.Name}\n  Nickname: {identityRecord.Nickname}\n  Thumbprint: {identityRecord.Thumbprint}");
    }, nameArgument, nicknameOption);
    rootCommand.AddCommand(createIdentityCommand);

    return rootCommand;
}
