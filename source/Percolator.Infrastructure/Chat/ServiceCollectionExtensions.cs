using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Apps.Chat;
using Percolator.Application.Apps.Chat.Queries;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Infrastructure.Chat.Queries;
using ChatApp = Percolator.Chat.App;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDirectConversationRepository, SqliteDirectConversationRepository>();
        services.AddScoped<IGroupConversationRepository, SqliteGroupConversationRepository>();
        services.AddScoped<IMessageRepository, SqliteMessageRepository>();
        services.AddScoped<ChatApp.IDirectConversationResolver, ChatConversationResolver>();
        services.AddScoped<ChatApp.IChatMessageWriter, SqliteChatMessageWriter>();
        services.AddScoped<IDirectSessionConversationLookup, SqliteDirectSessionConversationLookup>();
        services.AddScoped<ChatApp.IGroupCryptoStateRepository, SqliteGroupCryptoStateRepository>();
        services.AddScoped<IPendingGroupInvitationRepository, SqlitePendingGroupInvitationRepository>();
        services.AddScoped<IPendingGroupInvitationQueries, SqlitePendingGroupInvitationQueries>();
        services.AddScoped<IConversationMessageQueries, SqliteConversationMessageQueries>();
        services.AddScoped<IConversationMemberQueries, SqliteConversationMemberQueries>();
        return services;
    }
}
