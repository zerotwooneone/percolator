using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;
using Percolator.Chat.App;

namespace Percolator.Infrastructure.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICertificateFactory, FileBasedCertificateFactory>();
        services.AddScoped<IPreKeyBundleRepository, SqlitePreKeyBundleRepository>();
        services.AddScoped<IAdminSignatureVerifier, AdminSignatureVerifier>();
        services.AddScoped<IPendingSessionRepository, SqlitePendingSessionRepository>();
        services.AddScoped<ISessionRepository, SqliteSessionRepository>();
        services.AddSingleton<IX3dhDeriver, X3dhDeriver>();
        services.AddSingleton<IPreKeyBundleValidator, PreKeyBundleValidator>();
        return services;
    }
}
