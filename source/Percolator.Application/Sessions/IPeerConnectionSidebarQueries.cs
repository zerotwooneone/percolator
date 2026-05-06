namespace Percolator.Application.Sessions;

public interface IPeerConnectionSidebarQueries
{
    Task<IReadOnlyList<SidebarPeerConnectionDto>> LoadSidebarConnectionsAsync(int selfIdentityId, CancellationToken cancellationToken = default);
}
