using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Configuration;
using Percolator.Application.PeerDiscovery;
using Percolator.Network;
using Microsoft.Extensions.Logging;

namespace Percolator.Application.Network;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetworkServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind the configuration section to the config object
        var config = new PeerDiscoveryConfig();
        configuration.GetSection("PeerDiscovery").Bind(config);
        services.AddSingleton<IPeerDiscoveryConfig>(config);

        services.Configure<NodeOptions>(configuration.GetSection(NodeOptions.SectionName));

        // Register the adapter that bridges the Network and Cryptography domains
        services.AddSingleton<ISigningService, SigningService>();

        // Register the core service from the Network domain library
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();

        // Register the shared certificate manager as the central certificate authority
        services.AddSingleton<SharedCertificateManager>();

        // Register the trust store with the shared certificate manager
        services.AddSingleton<IPeerTrustManager>(provider => new InMemoryPeerTrustStore(
            provider.GetRequiredService<ITrustedPeerStore>(),
            provider.GetRequiredService<ILogger<InMemoryPeerTrustStore>>(),
            provider.GetRequiredService<SharedCertificateManager>()
        ));

        // Register our network communication services
        services.AddSingleton<ITlsHandshakeService, TlsHandshakeService>();
        services.AddSingleton<IGrpcSessionService, GrpcSessionService>();
        // Domain-level route planner for PeerRoutingProfile selection
        services.AddSingleton<IProfileRoutePlanner, SimpleRoutePlanner>();

        // Register a named HttpClient with simplified TLS validation logic using shared certificate
        services.AddHttpClient("percolator-grpc", (serviceProvider, client) =>
            {
                // Client configuration can be done here if needed
            })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<HttpClient>>();
                var sharedCertificateManager = serviceProvider.GetRequiredService<SharedCertificateManager>();

                var handler = new HttpClientHandler();

                // Add the shared client certificate
                try
                {
                    var clientCertificate = sharedCertificateManager.GetClientCertificate();
                    if (clientCertificate != null)
                    {
                        logger.LogDebug("Adding shared certificate with thumbprint {Thumbprint} to HttpClient", 
                            clientCertificate.Thumbprint);
                        handler.ClientCertificates.Add(clientCertificate);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load shared certificate for HttpClient");
                }

                // Simplified certificate validation - just check thumbprint against our shared certificate
                handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                {
                    if (cert is null)
                    {
                        logger.LogWarning("Server certificate is null.");
                        return false;
                    }

                    try
                    {
                        // Get our shared certificate thumbprint
                        var sharedCert = sharedCertificateManager.GetServerCertificate();
                        var isSharedCertificate = cert.Thumbprint.Equals(sharedCert.Thumbprint, StringComparison.OrdinalIgnoreCase);
                        
                        if (isSharedCertificate)
                        {
                            logger.LogDebug("Server certificate validated - matches our shared certificate thumbprint");
                            return true;
                        }
                        
                        logger.LogWarning("Server certificate does not match our shared certificate. Expected: {ExpectedThumbprint}, Actual: {ActualThumbprint}", 
                            sharedCert.Thumbprint, cert.Thumbprint);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during certificate validation");
                        return false;
                    }
                };

                return handler;
            });

        // Register the hosted service that runs the discovery (can be disabled in tests)
        var discoveryEnabled = configuration.GetValue<bool>("PeerDiscovery:Enabled", true);
        if (discoveryEnabled)
        {
            services.AddHostedService<PeerDiscoveryHostedService>();
        }

        services.AddScoped<IMessageTransportService, GrpcMessageTransportService>();
        services.AddSingleton<PercolatorMessageService>();
        services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();

        // Relay orchestrator for queued messages ACK flow
        services.AddSingleton<RelayOrchestrator>();

        // Network messaging strategy services (legacy components removed)

        // Application-layer envelope sender
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<Handshake.IInitiatorHelloService, Handshake.InitiatorHelloService>();
        services.AddTransient<IRemoteEnvelopeSender, RemoteEnvelopeSender>();

        // Establish direct session flow: service that creates peer/route and enqueues crypto pending session
        services.AddScoped<IEstablishDirectSessionService, EstablishDirectSessionService>();

        // Register domain Network.Messaging components required by MessageService
        services.AddSingleton<Percolator.Network.Messaging.IRelayTopology, Percolator.Network.Messaging.DefaultRelayTopology>();
        services.AddSingleton<Percolator.Network.Messaging.ITransportPort, NetworkTransportPortAdapter>();
        services.AddSingleton<Percolator.Network.Messaging.ISendExecutor, Percolator.Network.Messaging.DefaultSendExecutor>();
        services.AddSingleton<Percolator.Network.Messaging.INetworkSender, Percolator.Network.Messaging.DefaultNetworkSender>();

        return services;
    }
}
