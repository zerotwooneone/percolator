using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Dht;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Sessions;
using Percolator.Infrastructure.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Infrastructure.Network.Handshake;

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

        // Security/adapters
        services.AddScoped<Percolator.Application.Apps.Chat.IAtRestKeyProvider, Percolator.Infrastructure.Security.AtRestKeyProvider>();

        // Handshake pre-session store
        services.AddScoped<IPreHandshakeSessionStore, PreHandshakeSessionStore>();

        return services;
    }
}
