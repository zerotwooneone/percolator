namespace Percolator.Application.Chat;

public interface ISelfIdentityQueries
{
    Task<byte[]?> GetRelayRootKeyAsync(CancellationToken ct);
}
