using Percolator.Application.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application.Tests.TestDoubles;

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
