using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddScoped<IDirectSessionManager, DirectSessionManager>();
        services.AddScoped<IConversationService, ConversationService>();

        return services;
    }
}
