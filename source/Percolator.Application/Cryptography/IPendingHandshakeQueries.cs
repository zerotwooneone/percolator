using Percolator.Cryptography;
using PeerId = Percolator.Cryptography.Primitives.PeerId;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Cryptography;

public interface IPendingHandshakeQueries
{
    IAsyncEnumerable<PendingHandshake> EnumerateOpenAsync(CancellationToken cancellationToken=default);
}

public record PendingHandshake
{
    public required PendingSessionId Id;
    public required PeerId RemotePeer;
    public required string PeerName;
    public required RequestCorrelationId RequestCorrelationId;
    public required string? InviterFingerprintHex;
    public required DateTimeOffset CreatedAtUtc;
    public required DateTimeOffset? ExpiresAtUtc;
}