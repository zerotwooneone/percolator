namespace Percolator.Application.Network;

public interface IReservedPortQuery
{
    Task<IEnumerable<int>> GetReservedPortsAsync(CancellationToken ct);
}