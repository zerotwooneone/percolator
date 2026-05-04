using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Configuration;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Apps.Chat.Handlers;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Ingress;
using Percolator.Application.Network;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.RateLimiting;
using Percolator.Application.ReverseSignal;
using Percolator.Application.Sessions;
using Percolator.Chat.App.Handlers;
using Percolator.Dht.Messages;

namespace Percolator.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Register services from each of the application layers
        services.Configure<TransportOptions>(configuration.GetSection(TransportOptions.SectionName));
        services.Configure<ReverseSignalOptions>(configuration.GetSection(ReverseSignalOptions.SectionName));
        services.AddCryptographyServices(configuration);
        services.AddIdentityServices();
        services.AddIngressServices();
        services.AddNetworkServices(configuration);
        services.AddPeerDiscoveryServices();
        services.AddSessionServices();
        services.AddRateLimiting();
        
        services.AddScoped<IIdentityOrchestrator, IdentityOrchestrator>();

        services.AddChatServices();

        services.AddScoped<ReverseSignal.PendingSessionPurgeService>();
        services.AddSingleton<ICallbackEndpointValidator, CallbackEndpointValidator>();

        services.AddMediatR(cfg =>
        {
            // Scan current Application assembly for handlers (e.g., EstablishDirectSessionHandler)
            cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly);
            // Also include DHT assembly where request/notification handlers live
            cfg.RegisterServicesFromAssembly(typeof(PingRequest).Assembly);
            // Include Chat handler assemblies
            cfg.RegisterServicesFromAssembly(typeof(PostTextMessageHandler).Assembly);
            cfg.RegisterServicesFromAssembly(typeof(ReceiveTextMessageHandler).Assembly);
            // Include Prekey handlers assembly (Percolator.Prekey)
            cfg.RegisterServicesFromAssembly(typeof(Percolator.Prekey.Handlers.SubmitPreKeyBundleHandler).Assembly);
            // Include MessageQueue assembly (Percolator.MessageQueue) without referencing removed handler type
            cfg.RegisterServicesFromAssembly(typeof(Percolator.MessageQueue.DependencyInjection.ServiceCollectionExtensions).Assembly);
        });

        return services;
    }
}