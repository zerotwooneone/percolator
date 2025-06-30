using Microsoft.Extensions.DependencyInjection;
using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IDoubleRatchetSessionStore, InMemoryDoubleRatchetSessionStore>();
        services.AddSingleton<DirectSessionManager>();
        return services;
    }
}
