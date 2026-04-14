namespace Desktop.Wpf.Features.Sessions.Queries;

public interface IPeerConnectionQueries
{
    Task<IReadOnlyList<PeerConnectionStateSnapshot>> LoadAllConnectionsAsync(int selfIdentityId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingInboundSnapshot>> LoadPendingInboundAsync(CancellationToken cancellationToken = default);
}
