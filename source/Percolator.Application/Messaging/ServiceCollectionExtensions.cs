using Microsoft.Extensions.DependencyInjection;
using Percolator.Messaging;

namespace Percolator.Application.Messaging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddSingleton<IMessageService, MessageService>();
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();
        services.AddSingleton<IGroupService, GroupService>();
        //services.AddSingleton<MessagingGrpcService>();

        return services;
    }
}
