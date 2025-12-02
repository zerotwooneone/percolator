using Microsoft.Extensions.DependencyInjection;
using Percolator.Chat;
using Percolator.Infrastructure.Chat;
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
        services.AddScoped<ChatApp.IGroupAdminKeyStore, SqliteGroupAdminKeyStore>();
        services.AddScoped<ChatApp.IGroupAdminOpStore, SqliteGroupAdminOpStore>();
        services.AddScoped<ChatApp.IGroupAdminStateStore, SqliteGroupAdminStateStore>();
        services.AddScoped<IGroupManagerStateStore, SqliteGroupManagerStateStore>();
        services.AddScoped<IGroupSenderKeyRepository, InMemoryGroupSenderKeyRepository>();
        services.AddScoped<IDirectSessionConversationLookup, SqliteDirectSessionConversationLookup>();
        services.AddScoped<IActingAdminResolver, SqliteActingAdminResolver>();
        services.AddScoped<ChatApp.IKeyAdoptionStore, SqliteKeyAdoptionStore>();
        services.AddScoped<ChatApp.IAdminOperations, ChatApp.Services.AdminOperations>();
        return services;
    }
}
