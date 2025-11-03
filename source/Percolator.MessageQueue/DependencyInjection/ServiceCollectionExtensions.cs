using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Percolator.MessageQueue.Abstractions;
using Percolator.MessageQueue;

namespace Percolator.MessageQueue.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMessageQueue(this IServiceCollection services)
    {
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ServiceCollectionExtensions).Assembly));
        services.AddSingleton<IMessageQueueService, MessageQueueService>();
        return services;
    }
}
