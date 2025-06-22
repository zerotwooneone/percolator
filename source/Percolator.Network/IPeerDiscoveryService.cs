namespace Percolator.Network;

public interface IPeerDiscoveryService
{
    Task StartAsync(CancellationToken cancellationToken = default);
    void Stop();
}
