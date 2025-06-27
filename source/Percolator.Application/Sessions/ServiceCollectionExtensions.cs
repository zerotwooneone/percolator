using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessions(this IServiceCollection services)
    {
        services.AddSingleton<ILocalPeerProvider, LocalPeerProvider>();
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<IMessageRepository, InMemoryMessageRepository>();

        return services;
    }
}
