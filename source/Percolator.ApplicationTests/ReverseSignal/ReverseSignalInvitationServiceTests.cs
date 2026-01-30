using System;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Percolator.Application.ReverseSignal;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.ReverseSignal;

[TestFixture]
public class ReverseSignalInvitationServiceTests
{
    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    [Test]
    public async Task CreatePendingAsync_Persists_Pending_With_TTL()
    {
        var repo = new Mock<IPendingSessionRepository>(MockBehavior.Strict);
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        PendingSession? saved = null;
        repo.Setup(r => r.AddAsync(It.IsAny<PendingSession>(), It.IsAny<CancellationToken>()))
            .Callback<PendingSession, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        var svc = new ReverseSignalInvitationService(repo.Object, clock);
        var invitation = new HandshakeInvitation(new byte[] { 0xAA, 0xBB });
        var id = await svc.CreatePendingAsync(PeerId.NewId(), new ProtocolVersion(1), invitation, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.That(id, Is.Not.EqualTo(default(PendingSessionId)));
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.Id, Is.EqualTo(id));
        Assert.That(saved!.CreatedAtUtc, Is.EqualTo(clock.UtcNow));
        Assert.That(saved!.RequestCorrelationId, Is.Not.Null);
        Assert.That(saved!.ExpiresAtUtc.HasValue, Is.True);
        Assert.That(saved!.ExpiresAtUtc!.Value, Is.GreaterThan(saved!.CreatedAtUtc));
        repo.VerifyAll();
    }
}
