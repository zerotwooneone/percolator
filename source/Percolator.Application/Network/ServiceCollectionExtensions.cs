using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Configuration;
using Percolator.Application.PeerDiscovery;
using Percolator.Network;
using Microsoft.Extensions.Logging;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Percolator.Application.Identity;

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
        
        services.Configure<NodeOptions>(configuration.GetSection(NodeOptions.SectionName));

        // Register the core service from the Network domain library
        services.AddSingleton<IPeerDiscoveryService, PeerDiscoveryService>();

        services.AddSingleton<IPeerTrustManager, InMemoryPeerTrustStore>();

        // Register our network communication services
        services.AddSingleton<ITlsHandshakeService, TlsHandshakeService>();
        services.AddSingleton<IGrpcSessionService, GrpcSessionService>();

        // Register a named HttpClient with our custom TLS validation logic.
        // This will be used by ConversationService to create gRPC channels on the fly.
        services.AddHttpClient("percolator-grpc", (serviceProvider, client) =>
            {
                // Client configuration can be done here if needed
            })
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<HttpClient>>();
                var peerTrustManager = serviceProvider.GetRequiredService<IPeerTrustManager>();

                var handler = new HttpClientHandler();

                // Add the client certificate to the handler
                var activeIdentity = serviceProvider.GetRequiredService<ActiveIdentityContext>();
                var tlsCertificateService = serviceProvider.GetRequiredService<ITlsCertificateService>();
                if (activeIdentity.Identity is not null && activeIdentity.Keys is not null)
                {
                    var clientCert = tlsCertificateService.GetOrCreateTlsCertificateAsync(
                        activeIdentity.Identity.Name,
                        activeIdentity.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo())
                        .GetAwaiter().GetResult();
                    var clientCertificate = new X509Certificate2(clientCert.Export(X509ContentType.Pfx));
                    if (clientCertificate != null)
                    {
                        handler.ClientCertificates.Add(clientCertificate);
                    }
                }

                handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                {
                    if (cert is null)
                    {
                        logger.LogWarning("Server certificate is null.");
                        return false;
                    }

                    if (errors == SslPolicyErrors.None)
                    {
                        logger.LogDebug("Server certificate is valid by default policy.");
                        return true;
                    }

                    if (errors == SslPolicyErrors.RemoteCertificateChainErrors)
                    {
                        logger.LogDebug("Accepting self-signed certificate based on custom trust store.");
                        return peerTrustManager.IsTrusted(cert);
                    }

                    logger.LogWarning("Server certificate validation failed for thumbprint {Thumbprint} with errors: {Errors}", cert.Thumbprint, errors);
                    return false;
                };

                return handler;
            });

        // Register the hosted service that runs the discovery
        services.AddHostedService<PeerDiscoveryHostedService>();

        services.AddSingleton<IMessageTransportService, GrpcMessageTransportService>();
        services.AddSingleton<PercolatorMessageService>();
        services.AddSingleton<IPeerDiscoveryHandler, PeerDiscoveryHandler>();

        return services;
    }
}
