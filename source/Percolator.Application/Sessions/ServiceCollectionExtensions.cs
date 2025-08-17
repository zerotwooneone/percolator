using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;

namespace Percolator.Application.Sessions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSessionServices(this IServiceCollection services)
    {
        services.AddSingleton<IDirectSessionManager, DirectSessionManager>();
        services.AddSingleton<IConversationService, ConversationService>();
        services.AddSingleton<IMessageService, MessageService>();

        return services;
    }
}
