using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Dht;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Sessions;
using Percolator.Infrastructure.Network;
using Percolator.Infrastructure.MessageQueue;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Network.Handshake;
using Percolator.Application.KeyExchange;
using Percolator.Application.Apps.Chat;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Network.Grpc;
using Percolator.Infrastructure.Network.Tls;
using Percolator.Infrastructure.Network.Trust;
using Percolator.Network.Services;
using Percolator.Application.Network;
using Percolator.Network;

namespace Percolator.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Ensure IConfiguration is available from DI
        services.AddSingleton(configuration);
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
        services.AddChatInfrastructure();
        services.AddSessionsInfrastructure();
        services.AddIdentityInfrastructure();
        services.AddNetworkInfrastructure();
        services.AddDhtInfrastructure();
        services.AddCryptographyInfrastructure();
        services.AddMessageQueueInfrastructure();

        // Network infrastructure services (gRPC, TLS, Trust)
        services.AddSingleton<SharedCertificateManager>();
        services.AddSingleton<IPeerTrustManager, InMemoryPeerTrustStore>(provider => new InMemoryPeerTrustStore(
            provider.GetRequiredService<ITrustedPeerStore>(),
            provider.GetRequiredService<ILogger<InMemoryPeerTrustStore>>(),
            provider.GetRequiredService<SharedCertificateManager>()
        ));
        services.AddSingleton<ITlsHandshakeService, TlsHandshakeService>();
        
        // Bind Domain Interfaces to Infrastructure Implementations
        services.AddSingleton<ISessionEstablishmentTransport, GrpcSessionService>();
        services.AddScoped<IMessageTransportService, GrpcMessageTransportService>();

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

        // Security/adapters
        services.AddScoped<IAtRestKeyProvider, AtRestKeyProvider>();

        // Handshake pre-session store
        services.AddScoped<IPreHandshakeSessionStore, PreHandshakeSessionStore>();

        // Self pre-key storage (local private keys) now backed by Sqlite
        services.AddScoped<ISelfPreKeyBundleRepository, Cryptography.SqliteSelfPreKeyBundleRepository>();

        return services;
    }
}
