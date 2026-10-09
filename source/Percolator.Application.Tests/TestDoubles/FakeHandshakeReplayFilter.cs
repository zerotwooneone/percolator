using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakeHandshakeReplayFilter : IHandshakeReplayFilter
{
    private readonly HashSet<DhPublicKey> _seen = [];
    public bool AllowAll { get; set; } = false;

    public bool TryRecordAndValidate(DhPublicKey ephemeralKey, DateTimeOffset receivedAtUtc)
    {
        if (AllowAll) return true;
        return _seen.Add(ephemeralKey);
    }
}
