using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Identities;

namespace Percolator.Apps.Chat.Features.Introductions;

public interface IPeerIntroductionConsentRepository
{
    Task AddAsync(PeerIntroductionConsent consent, CancellationToken ct = default);
    Task UpdateAsync(PeerIntroductionConsent consent, CancellationToken ct = default);
    Task<PeerIntroductionConsent?> GetByIdAsync(string requestId, CancellationToken ct = default);
    Task<IReadOnlyList<PeerIntroductionConsent>> GetPendingInboundRequestsAsync(PublicIdentityId localIdentityId, CancellationToken ct = default);
    Task<IReadOnlyList<PeerIntroductionConsent>> GetAllAsync(PublicIdentityId localIdentityId, CancellationToken ct = default);
}
