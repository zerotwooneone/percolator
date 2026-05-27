using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Chat.App;
using Percolator.Application.Apps.Chat.Handlers;

namespace Percolator.Application.Apps.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatServices(this IServiceCollection services)
    {
        services.AddSingleton<ISelfParticipantIdProvider>(s=> s.GetRequiredService<ActiveIdentityContext>());
        services.AddScoped<IEnvelopeCrypto, DummyEnvelopeCrypto>();
        services.AddScoped<IPkhPeerResolver, PkhPeerResolver>();

        // Register SendGroupMessageCommandHandler
        services.AddScoped<IRequestHandler<Commands.SendGroupMessageCommand>, SendGroupMessageCommandHandler>();

        return services;
    }
}
