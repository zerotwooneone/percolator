using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyServices(this IServiceCollection services, bool enableCryptoLogging)
    {
        services.AddSingleton<ISigningService, EcdsaSigningService>();
        services.AddSingleton<IX3DHManager,X3DHManager>();
        
        services.Configure<CryptographyOptions>(options => {
            options.EnableCryptographicMaterialLogging = enableCryptoLogging;
        
            // If crypto logging is enabled, log a warning
            if (enableCryptoLogging)
            {
                var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("CryptographyOptions");
                logger.LogWarning("SECURITY WARNING: Cryptographic material hash logging is ENABLED. This should only be used for debugging.");
            }
        });
        
        return services;
    }
}
