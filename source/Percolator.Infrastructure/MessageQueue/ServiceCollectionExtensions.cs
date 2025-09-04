using Microsoft.Extensions.DependencyInjection;
using Percolator.MessageQueue.Abstractions;

namespace Percolator.Infrastructure.MessageQueue;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessageQueueInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IMessageQueueRepository, SqliteMessageQueueRepository>();
        return services;
    }
}
