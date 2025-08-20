using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ICertificateFactory, FileBasedCertificateFactory>();
        services.AddScoped<IPreKeyBundleRepository, SqlitePreKeyBundleRepository>();
        return services;
    }
}
