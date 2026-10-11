using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery;

/// <summary>
/// Plugin entry point for Discovery application (AppId = 0x02).
/// </summary>
public sealed class DiscoveryPlugin : IAppPlugin
{
    public AppId Id => AppId.Discovery;
    public string Name => "Discovery";

    public Task StartAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
