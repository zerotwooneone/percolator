using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application.Ports;

/// <summary>
/// Filters out replayed or duplicate handshake invitations to prevent CPU exhaustion.
/// </summary>
public interface IHandshakeReplayFilter
{
    bool TryRecordAndValidate(DhPublicKey ephemeralKey, DateTimeOffset receivedAtUtc);
}
