using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Network;
using Percolator.Infrastructure.Network.Grpc;
using Percolator.Network;

namespace Percolator.Infrastructure.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDirectSessionRepository, SqliteDirectSessionRepository>();
        // New Network domain repositories
        services.AddScoped<IPeerRoutingProfileRepository, SqlitePeerRoutingProfileRepository>();
        services.AddScoped<IPeerRouteCandidateRepository, SqlitePeerRouteCandidateRepository>();
        services.AddScoped<IDiscoveredPeerRepository, SqliteDiscoveredPeerRepository>();
        
        services.AddScoped<IReservedPortQuery, SqliteReservedPortQuery>();

        // gRPC channel factory and session service
        services.AddSingleton<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
        services.AddScoped<GrpcSessionService>();

        // Relay transport client
        services.AddScoped<Percolator.Application.Chat.IRelayTransportClient, Percolator.Infrastructure.Network.Grpc.RelayTransportClient>();

        // Relay group stream dispatcher
        services.AddSingleton<IRelayGroupStreamDispatcher, GrpcRelayGroupStreamDispatcher>();

        return services;
    }
}
