using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatApp(this IServiceCollection services)
    {
        services.AddSingleton<IAppPlugin, ChatPlugin>();
        services.AddSingleton<IAppPayloadHandler, ChatPayloadHandler>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<ChatMediaService>();
        services.AddScoped<GroupInvitationService>();

        return services;
    }
}
