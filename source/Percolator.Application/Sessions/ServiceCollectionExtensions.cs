using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.KeyExchange;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Sessions;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<IX3DHManager, X3DHManager>();

        // TODO: Move to application layer
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<IDoubleRatchetProtocol, DoubleRatchetProtocolAdapter>();

        return services;
    }
}
