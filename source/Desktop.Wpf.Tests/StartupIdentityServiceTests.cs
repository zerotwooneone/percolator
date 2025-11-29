using System;
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
        var existing = new SelfIdentity(new SelfId(123));
        existing.SetDisplayName("Alice");
        existing.TouchLastUsed(now.AddDays(-1));
        repo.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var svc = new StartupIdentityService(repo.Object, () => now);

        // Act
        var result = await svc.ResolveOrCreateAsync();

        // Assert
        Assert.That(result, Is.EqualTo(existing));
        repo.Verify(r => r.SaveAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ResolveOrCreateAsync_WhenNoneExists_CreatesAndSavesNewWithLastUsedNow()
    {
        // Arrange
        var now = new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var repo = new Mock<ISelfIdentityRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetMostRecentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SelfIdentity?)null);
        SelfIdentity? saved = null;
        repo.Setup(r => r.SaveAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()))
            .Callback<SelfIdentity, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);
        var svc = new StartupIdentityService(repo.Object, () => now);

        // Act
        var result = await svc.ResolveOrCreateAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.LastUsedUtc, Is.EqualTo(now));
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.LastUsedUtc, Is.EqualTo(now));
        repo.Verify(r => r.SaveAsync(It.IsAny<SelfIdentity>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
