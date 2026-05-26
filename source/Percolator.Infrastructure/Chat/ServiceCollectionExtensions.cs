using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;
using Percolator.Application.Apps.Chat;
using ChatApp = Percolator.Chat.App;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IConversationRepository, SqliteConversationRepository>();
        services.AddScoped<ChatApp.IConversationResolver, ChatConversationResolver>();
        services.AddScoped<ChatApp.IChatMessageWriter, SqliteChatMessageWriter>();
        services.AddScoped<IDirectSessionConversationLookup, SqliteDirectSessionConversationLookup>();
        services.AddScoped<ChatApp.IGroupCryptoStateRepository, SqliteGroupCryptoStateRepository>();
        return services;
    }
}
