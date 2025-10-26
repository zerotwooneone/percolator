using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Configuration;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.RateLimiting;
using Percolator.Application.Sessions;
using Percolator.Chat.App.Commands;
using Percolator.Dht.Messages;

namespace Percolator.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Register services from each of the application layers
        services.Configure<TransportOptions>(configuration.GetSection(TransportOptions.SectionName));
        services.AddCryptographyServices(configuration);
        services.AddIdentityServices();
        services.AddKeyExchangeServices();
        services.AddNetworkServices(configuration);
        services.AddPeerDiscoveryServices();
        services.AddSessionServices();
        services.AddRateLimiting();
        
        services.AddSingleton<ActiveIdentityContext>();
        
        services.AddScoped<IIdentityOrchestrator, IdentityOrchestrator>();

        services.AddChatServices();

        services.AddMediatR(cfg =>
        {
            // Scan current Application assembly for handlers (e.g., EstablishDirectSessionHandler)
            cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly);
            // Also include DHT assembly where request/notification handlers live
            cfg.RegisterServicesFromAssembly(typeof(PingRequest).Assembly);
            // Include Chat handlers assembly (Percolator.Chat)
            cfg.RegisterServicesFromAssembly(typeof(PostTextMessageHandler).Assembly);
            // Include Prekey handlers assembly (Percolator.Prekey)
            cfg.RegisterServicesFromAssembly(typeof(Percolator.Prekey.Handlers.SubmitPreKeyBundleHandler).Assembly);
            // Include MessageQueue handlers assembly (Percolator.MessageQueue)
            cfg.RegisterServicesFromAssembly(typeof(Percolator.MessageQueue.Handlers.EnqueueOpaqueMessageHandler).Assembly);
        });

        return services;
    }
}