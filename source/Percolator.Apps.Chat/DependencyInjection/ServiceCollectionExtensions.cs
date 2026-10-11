using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.Chat.Handlers;
using Percolator.Apps.Chat.Ports;
using Percolator.Apps.Chat.Services;
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
