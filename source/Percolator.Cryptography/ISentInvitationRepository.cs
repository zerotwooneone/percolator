using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public interface ISentInvitationRepository
{
    Task UpsertAsync(SentInvitation invitation, CancellationToken cancellationToken = default);
    Task<SentInvitation?> TryGetAsync(RequestCorrelationId requestCorrelationId, CancellationToken cancellationToken = default);
    Task DeleteAsync(RequestCorrelationId requestCorrelationId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SentInvitation> EnumerateExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
