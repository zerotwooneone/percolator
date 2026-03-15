using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.Cryptography;

public interface IPendingSessionQueries
{
    Task<PendingSessionId?> TryGetByRequestCorrelationIdAsync(
        RequestCorrelationId requestCorrelationId,
        CancellationToken cancellationToken = default);
}
