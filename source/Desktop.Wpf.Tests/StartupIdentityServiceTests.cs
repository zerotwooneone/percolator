using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Desktop.Wpf.Tests;

[TestFixture]
public class StartupIdentityServiceTests
{
    
    [Test]
    public async Task ResolveOrCreateAsync_WhenMruExists_ReturnsIt_WithoutCreating()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var repo = new Mock<ISelfIdentityRepository>(MockBehavior.Strict);
        var networkEnv = new Mock<Percolator.Application.Network.INetworkEnvironment>(MockBehavior.Strict);
        var existing = new SelfIdentity(new SelfId(123), new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000));
        existing.SetDisplayName("Alice");
        existing.TouchLastUsed(now.AddDays(-1));
        repo.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var svc = new StartupIdentityService(repo.Object, new TestClock(now), networkEnv.Object, new Mock<Percolator.Application.Network.IReservedPortQuery>().Object);

        // Act
        var result = await svc.ResolveOrCreateAsync();

        // Assert
        Assert.That(result, Is.EqualTo(existing));
        repo.Verify(r => r.CreateAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
        repo.Verify(r => r.GetByIdAsync(It.IsAny<SelfId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrCreateAsync_WhenNoneExists_CreatesAndSavesNewWithLastUsedNow()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var repo = new Mock<ISelfIdentityRepository>(MockBehavior.Strict);
        var networkEnv = new Mock<Percolator.Application.Network.INetworkEnvironment>(MockBehavior.Strict);
        networkEnv.Setup(n => n.GetAvailablePortAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(5000);
        repo.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SelfIdentity?)null);
        SelfIdentity? saved = null;
        var newId = new SelfId(99);
        repo.Setup(r => r.CreateAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<SelfIdentity, CancellationToken>((s, _) => saved = s)
            .ReturnsAsync(newId);
        repo.Setup(r => r.GetByIdAsync(newId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var created = new SelfIdentity(newId, new PublicIdentityId(Guid.NewGuid()), new ListeningPort(5000));
                created.TouchLastUsed(now);
                return created;
            });
        var svc = new StartupIdentityService(repo.Object, new TestClock(now), networkEnv.Object, new Mock<Percolator.Application.Network.IReservedPortQuery>().Object);

        // Act
        var result = await svc.ResolveOrCreateAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.LastUsedUtc, Is.EqualTo(now));
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.LastUsedUtc, Is.EqualTo(now));
        repo.Verify(r => r.CreateAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.GetByIdAsync(newId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
