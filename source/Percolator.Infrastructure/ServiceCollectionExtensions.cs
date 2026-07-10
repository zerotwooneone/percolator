using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Dht;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Sessions;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.Network.Certificates;
using Percolator.Infrastructure.MessageQueue;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Network.Handshake;
using Percolator.Application.KeyExchange;
using Percolator.Infrastructure.Network.Grpc;
using Percolator.Application.Network;
using Percolator.Network.Services;
using Percolator.Cryptography;

namespace Percolator.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Ensure IConfiguration is available from DI
        services.AddSingleton(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Configure(options =>
            {
                if (!Path.IsPathRooted(options.Path))
                {
                    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    options.Path = Path.Combine(appDataPath, options.Path);
                }
            });
        services.AddOptions<TlsOptions>()
            .Bind(configuration.GetSection(TlsOptions.SectionName));
        services.AddChatInfrastructure();
        services.AddSessionsInfrastructure();
        services.AddIdentityInfrastructure();
        services.AddNetworkInfrastructure();
        services.AddDhtInfrastructure();
        services.AddCryptographyInfrastructure();
        services.AddMessageQueueInfrastructure();

        // Bind Domain Interfaces to Infrastructure Implementations
        services.AddSingleton<ISessionEstablishmentTransport, GrpcSessionService>();
        services.AddScoped<IMessageTransportService, GrpcMessageTransportService>();

        // TLS gRPC infrastructure
        services.AddSingleton<ITransportCertificateProvider, TransportCertificateProvider>();
        services.AddSingleton<INetworkEnvironment, NetworkEnvironment>();
        services.AddSingleton<IPeerGrpcChannelFactory, PeerGrpcChannelFactory>();
        services.AddSingleton<IGrpcServerManager, GrpcServerManager>();
        services.AddHostedService<GrpcShutdownHostedService>();

        // Domain event handlers
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<ActiveIdentityLoadedEventHandler>());

        // gRPC Server Services
        services.AddScoped<Percolator.Infrastructure.Network.Grpc.PercolatorMessageService>();
        services.AddSingleton<Percolator.Infrastructure.Network.Grpc.IdentityReadinessInterceptor>();
        services.AddScoped<Percolator.Infrastructure.Network.Grpc.RelayGroupService>();

        // Peer Discovery Hosted Service (conditional on configuration)
        var discoveryEnabled = configuration.GetValue<bool>("PeerDiscovery:Enabled", true);
        if (discoveryEnabled)
        {
            services.AddHostedService<Percolator.Infrastructure.Network.PeerDiscoveryHostedService>();
        }

        // Handshake pre-session store
        services.AddScoped<IPreHandshakeSessionStore, PreHandshakeSessionStore>();

        // Self pre-key storage (local private keys) now backed by Sqlite
        services.AddScoped<ISelfPreKeyBundleRepository, Cryptography.SqliteSelfPreKeyBundleRepository>();

        // Ed25519 cryptography service (native interop implementation)
        services.AddSingleton<IEd25519CryptographyService, NativeEd25519CryptographyService>();

        return services;
    }
}
