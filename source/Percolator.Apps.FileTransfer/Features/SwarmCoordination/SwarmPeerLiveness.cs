using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class SwarmPeerLiveness
{
    public PublicIdentityId PeerId { get; }
    public DateTimeOffset LastSeenUtc { get; private set; }

    public SwarmPeerLiveness(PublicIdentityId peerId, DateTimeOffset lastSeenUtc)
    {
        PeerId = peerId;
        LastSeenUtc = lastSeenUtc;
    }

    public bool IsActive(DateTimeOffset now, TimeSpan timeout) => now - LastSeenUtc < timeout;
    public void RecordActivity(IDateTimeProvider clock) => LastSeenUtc = clock.UtcNow;
}
