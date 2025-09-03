using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Prekey.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrekey(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly));
        return services;
    }
}
