using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Domain.Identities;

namespace Percolator.Apps.Chat.Tests.TestDoubles;

public sealed class InMemoryPeerIntroductionConsentRepository : IPeerIntroductionConsentRepository
{
    private readonly ConcurrentDictionary<string, PeerIntroductionConsent> _storage = new();

    public IReadOnlyCollection<PeerIntroductionConsent> All => _storage.Values.ToList();

    public Task AddAsync(PeerIntroductionConsent consent, CancellationToken ct = default)
    {
        _storage[consent.RequestId] = consent;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(PeerIntroductionConsent consent, CancellationToken ct = default)
    {
        _storage[consent.RequestId] = consent;
        return Task.CompletedTask;
    }

    public Task<PeerIntroductionConsent?> GetByIdAsync(string requestId, CancellationToken ct = default)
    {
        _storage.TryGetValue(requestId, out var consent);
        return Task.FromResult(consent);
    }

    public Task<IReadOnlyList<PeerIntroductionConsent>> GetPendingInboundRequestsAsync(PublicIdentityId localIdentityId, CancellationToken ct = default)
    {
        var list = _storage.Values
            .Where(c => c.LocalIdentityId == localIdentityId &&
                        c.Status == IntroductionStatus.Pending &&
                        c.Direction == IntroductionDirection.InboundRequest)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<PeerIntroductionConsent>>(list);
    }

    public Task<IReadOnlyList<PeerIntroductionConsent>> GetAllAsync(PublicIdentityId localIdentityId, CancellationToken ct = default)
    {
        var list = _storage.Values
            .Where(c => c.LocalIdentityId == localIdentityId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<PeerIntroductionConsent>>(list);
    }
}
