using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Network;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind the configuration section to the config object
        var config = new PeerDiscoveryConfig();
        configuration.GetSection("PeerDiscovery").Bind(config);
        services.AddSingleton<IPeerDiscoveryConfig>(config);

        services.AddSingleton<ISigningService, SigningService>();

        // Register the core service from the Network domain library
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();

        // Register the hosted service that runs the discovery
        services.AddHostedService<PeerDiscoveryHostedService>();

        services.AddSingleton<IMessageTransportService, GrpcMessageTransportService>();
        services.AddSingleton<PercolatorMessageService>();
        return services;
    }
}
