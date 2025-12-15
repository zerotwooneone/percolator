using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Application.Ingress;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIngressServices(this IServiceCollection services)
    {
        services.AddScoped<IIngressReadinessGate, ActiveIdentityReadinessGate>();
        services.AddScoped<IIngressValidator, OpaquePayloadValidator>();
        services.AddScoped<IIngressPipeline, DefaultIngressPipeline>();
        services.AddScoped<IMessageIngress, MessageIngress>();
        services.AddScoped<IPendingHandshakeIngress, PendingHandshakeIngress>();
        return services;
    }
}
