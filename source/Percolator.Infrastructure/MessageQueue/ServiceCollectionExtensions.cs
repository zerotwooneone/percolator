using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Chat.MessageQueue;

namespace Percolator.Infrastructure.MessageQueue;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessageQueueInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IMessageQueueRepository, SqliteMessageQueueRepository>();
        return services;
    }
}
