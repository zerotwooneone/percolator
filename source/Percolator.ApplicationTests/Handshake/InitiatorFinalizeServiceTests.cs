using System.Runtime.CompilerServices;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;

namespace Percolator.ApplicationTests.Handshake;

[TestFixture]
public class InitiatorFinalizeServiceTests
{
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    private static async IAsyncEnumerable<PreHandshakeRecord> YieldAsync(
        PreHandshakeRecord record,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return record;
        await Task.CompletedTask;
    }
}
