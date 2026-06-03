namespace Percolator.Application.Network;

public interface INetworkEnvironment
{
    // Gets an available port in the configured range
    Task<int> GetAvailablePortAsync(IEnumerable<int> exclusionList,CancellationToken ct);
}
