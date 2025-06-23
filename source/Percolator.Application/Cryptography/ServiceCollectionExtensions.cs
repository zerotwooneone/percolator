using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptography(this IServiceCollection services)
    {
        services.AddSingleton<X3DHManager>();
        services.AddSingleton<ISigningService, EcdsaSigningService>();
        return services;
    }
}
