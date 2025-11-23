using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ISigningService, EcdsaSigningService>();
        
        // Bind the configuration section to the options class
        services.AddOptions<CryptographyOptions>()
            .Bind(config.GetSection(nameof(CryptographyOptions)));

        // Register the validator
        services.AddSingleton<IValidateOptions<CryptographyOptions>, CryptographyOptionsValidator>();
        
        // Configure the options
        services.Configure<CryptographyOptions>(options => {
            // If crypto logging is enabled, log a warning
            if (options.EnableCryptographicMaterialLogging)
            {
                var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("CryptographyOptions");
                logger.LogWarning("SECURITY WARNING: Cryptographic material hash logging is ENABLED. This should only be used for debugging.");
            }
        });
        
        return services;
    }
}
