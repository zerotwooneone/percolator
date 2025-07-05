using Microsoft.Extensions.DependencyInjection;
using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        // Register domain services and their in-memory stores
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        // Service registration for IDoubleRatchetSessionStore is now handled by the Infrastructure layer.
        services.AddSingleton<IMessageRepository, InMemoryMessageRepository>();

        // Register application services
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<ILocalPeerProvider, LocalPeerProvider>();

        // Register the main session orchestrator
        services.AddSingleton<DirectSessionManager>();

        return services;
    }
}
