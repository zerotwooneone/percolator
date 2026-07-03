using System.Runtime.CompilerServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MediatR;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Identity;
using Percolator.Network;
using PeerId = Percolator.Cryptography.Primitives.PeerId;

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
