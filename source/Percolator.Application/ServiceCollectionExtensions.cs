using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.PeerDiscovery;
using Percolator.Application.RateLimiting;
using Percolator.Application.Sessions;

namespace Percolator.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, 
        IConfiguration configuration)
    {
        // Register services from each of the application layers
        services.AddCryptographyServices(configuration);
        services.AddIdentityServices();
        services.AddKeyExchangeServices();
        services.AddNetworkServices(configuration);
        services.AddPeerDiscoveryServices();
        services.AddSessionServices();
        services.AddRateLimiting();
        services.AddChatServices();
        
        return services;
    }
}