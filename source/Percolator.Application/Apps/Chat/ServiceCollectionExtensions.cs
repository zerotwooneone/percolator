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
        services.AddScoped<IEnvelopeCrypto, DummyEnvelopeCrypto>();
        services.AddScoped<IPkhPeerResolver, PkhPeerResolver>();

        return services;
    }
}
