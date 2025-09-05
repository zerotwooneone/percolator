using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;
using Percolator.Chat.App;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IConversationRepository, SqliteConversationRepository>();
        services.AddScoped<IConversationResolver, ChatConversationResolver>();
        services.AddScoped<IChatMessageWriter, SqliteChatMessageWriter>();
        return services;
    }
}
