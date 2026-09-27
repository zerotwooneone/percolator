namespace Percolator.PluginSdk;

public interface IAppPlugin
{
    AppId Id { get; }
    string Name { get; }
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
