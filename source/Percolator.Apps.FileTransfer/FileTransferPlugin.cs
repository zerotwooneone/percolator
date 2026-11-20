using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer;

public sealed class FileTransferPlugin : IAppPlugin
{
    public AppId Id => AppId.FileTransferControl;
    public string Name => "FileTransfer";

    public Task StartAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
