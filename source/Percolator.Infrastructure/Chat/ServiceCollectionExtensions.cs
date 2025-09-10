using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Chat.App;
using Percolator.Infrastructure.Chat;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IConversationRepository, SqliteConversationRepository>();
        services.AddScoped<IConversationResolver, ChatConversationResolver>();
        services.AddScoped<IChatMessageWriter, SqliteChatMessageWriter>();
        services.AddScoped<IGroupAdminKeyStore, SqliteGroupAdminKeyStore>();
        services.AddScoped<IGroupAdminOpStore, SqliteGroupAdminOpStore>();
        return services;
    }
}
