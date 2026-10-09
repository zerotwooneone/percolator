using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Ports;

/// <summary>
/// Filters out replayed or duplicate handshake invitations to prevent CPU exhaustion.
/// </summary>
public interface IHandshakeReplayFilter
{
    bool TryRecordAndValidate(DhPublicKey ephemeralKey, DateTimeOffset receivedAtUtc);
}
