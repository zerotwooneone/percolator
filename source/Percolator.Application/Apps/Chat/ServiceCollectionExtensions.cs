using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.App;

namespace Percolator.Application.Apps.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatServices(this IServiceCollection services)
    {
        services.AddSingleton<ISelfParticipantIdProvider>(s=> s.GetRequiredService<ActiveIdentityContext>());
        services.AddScoped<IGroupKeyOperations, GroupKeyOperations>();
        services.AddSingleton<IGroupManagerResolver, PersistentGroupManagerResolver>();
        services.AddScoped<ITransportKeyResolver, TransportKeyResolver>();
        services.AddTransient<IAdminOperationDispatcher, AdminOperationDispatcher>();
        services.AddTransient<IAdminOperationSigner, AdminOperationSigner>();
        services.AddScoped<IAdminSequenceProvider, AdminSequenceProvider>();

        return services;
    }
}
