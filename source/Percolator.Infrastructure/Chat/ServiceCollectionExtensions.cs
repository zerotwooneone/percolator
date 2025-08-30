using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IConversationRepository, SqliteConversationRepository>();
        return services;
    }
}
