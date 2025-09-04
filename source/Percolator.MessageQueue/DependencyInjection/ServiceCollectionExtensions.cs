using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Percolator.MessageQueue.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessageQueue(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly));
        return services;
    }
}
