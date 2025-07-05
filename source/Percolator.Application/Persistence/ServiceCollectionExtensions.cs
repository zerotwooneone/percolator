using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;

namespace Percolator.Application.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersistenceServices(this IServiceCollection services)
    {
        services.AddSingleton<IConversationRepository, InMemoryConversationRepository>();
        return services;
    }
}
