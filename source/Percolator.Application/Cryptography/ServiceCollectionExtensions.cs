using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyServices(this IServiceCollection services)
    {
        services.AddSingleton<X3DHManager>();
        return services;
    }
}
