using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Chat;
using Percolator.Application.Identity;
using Percolator.Chat;
using Percolator.Application.Apps.Chat.Handlers;

namespace Percolator.Application.Apps.Chat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatServices(this IServiceCollection services)
    {
        services.AddSingleton<ISelfParticipantIdProvider>(s=> s.GetRequiredService<ActiveIdentityContext>());
        
        // Register SendGroupMessageCommandHandler
        services.AddScoped<IRequestHandler<Commands.SendGroupMessageCommand>, SendGroupMessageCommandHandler>();

        // Register Profile Orchestration Service
        services.AddScoped<IProfileOrchestrationService, ProfileOrchestrationService>();

        // Register Peer Authentication Service
        services.AddScoped<IPeerAuthenticationService, PeerAuthenticationService>();

        return services;
    }
}
