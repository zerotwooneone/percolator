using Percolator.PluginSdk;

namespace Percolator.Apps.Chat;

public sealed class ChatPlugin : IAppPlugin
{
    public AppId Id => AppId.Chat;
    public string Name => "Chat";

    public Task StartAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
