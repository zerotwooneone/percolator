using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Apps.Chat.Queries;
using Percolator.Application.Chat;
using Percolator.Chat;
using Percolator.Chat.GroupLedger;
using Percolator.Chat.Messaging.App;
using Percolator.Infrastructure.Chat.Queries;

namespace Percolator.Infrastructure.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IDirectConversationRepository, SqliteDirectConversationRepository>();
        services.AddScoped<IGroupConversationRepository, SqliteGroupConversationRepository>();
        services.AddScoped<IMessageRepository, SqliteMessageRepository>();
        services.AddScoped<IDirectConversationResolver, ChatConversationResolver>();
        services.AddScoped<IChatMessageWriter, SqliteChatMessageWriter>();
        services.AddScoped<IGroupCryptoStateRepository, SqliteGroupCryptoStateRepository>();
        services.AddScoped<IPendingGroupInvitationRepository, SqlitePendingGroupInvitationRepository>();
        services.AddScoped<IPendingGroupInvitationQueries, SqlitePendingGroupInvitationQueries>();
        services.AddScoped<IConversationMessageQueries, SqliteConversationMessageQueries>();
        services.AddScoped<IConversationMemberQueries, SqliteConversationMemberQueries>();
        return services;
    }
}
