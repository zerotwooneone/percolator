using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.KeyExchange;
using Percolator.Sessions;
using Percolator.Cryptography;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        // Register domain services and their in-memory stores
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();
        services.AddSingleton<IMessageRepository, InMemoryMessageRepository>();

        // Register key exchange services
        services.AddSingleton<IX3DHManager, X3DHManager>();

        // Register application services
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMessageService, MessageService>();

        return services;
    }
}
