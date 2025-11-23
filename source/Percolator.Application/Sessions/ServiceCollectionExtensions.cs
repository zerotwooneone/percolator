using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Services;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddScoped<IHandshakeService, HandshakeService>();
        services.AddScoped<ISecureMessagingService, SecureMessagingService>();
        services.AddScoped<IDirectSessionLocator, DirectSessionLocator>();

        return services;
    }
}
