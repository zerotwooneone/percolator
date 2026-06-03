using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Application.Messaging;
using Percolator.Identity;
using Percolator.Identity.DomainEvents;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.ApplicationTests.Identity;

[TestFixture]
public class IdentityOrchestratorTests
{
    private Mock<ISelfIdentityKeysStore> _keysStoreMock;
    private Mock<ISelfIdentityRepository> _selfIdentityRepositoryMock;
    private Mock<ILogger<IdentityOrchestrator>> _loggerMock;
    private Mock<IOptions<NodeOptions>> _optionsMock;
    private ActiveIdentityContext _activeIdentityContext;
    private Mock<IPublisher> _publisherMock;
    private IdentityOrchestrator _sut;

    [SetUp]
    public void Setup()
    {
        _keysStoreMock = new Mock<ISelfIdentityKeysStore>();
        _selfIdentityRepositoryMock = new Mock<ISelfIdentityRepository>();
        _loggerMock = new Mock<ILogger<IdentityOrchestrator>>();
        _optionsMock = new Mock<IOptions<NodeOptions>>();
        _activeIdentityContext = new ActiveIdentityContext();
        _publisherMock = new Mock<IPublisher>();

        _optionsMock.Setup(o => o.Value).Returns(new NodeOptions());

        _sut = new IdentityOrchestrator(
            _keysStoreMock.Object,
            _selfIdentityRepositoryMock.Object,
            _loggerMock.Object,
            _optionsMock.Object,
            _activeIdentityContext,
            _publisherMock.Object);
    }

    [Test]
    public async Task ResolveIdentityAsync_WhenIdentityFound_PublishesActiveIdentityLoadedEvent()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);
        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);
        domainIdentity.SetDisplayName("TestIdentity");

        var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = new X3dhKeys(ikSigning, spk);

        _selfIdentityRepositoryMock
            .Setup(r => r.GetByIdAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(domainIdentity);

        _keysStoreMock
            .Setup(k => k.LoadAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        // ACT
        await _sut.ResolveIdentityAsync(selfId, CancellationToken.None);

        // ASSERT
        _publisherMock.Verify(
            p => p.Publish(
                It.Is<DomainEventNotification<ActiveIdentityLoadedEvent>>(
                    n => n.DomainEvent.IdentityId == selfId),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ResolveIdentityAsync_WhenIdentityFound_PopulatesActiveIdentityContext()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);
        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);
        domainIdentity.SetDisplayName("TestIdentity");

        var ikSigning = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spk = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var keys = new X3dhKeys(ikSigning, spk);

        _selfIdentityRepositoryMock
            .Setup(r => r.GetByIdAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(domainIdentity);

        _keysStoreMock
            .Setup(k => k.LoadAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(keys);

        // ACT
        await _sut.ResolveIdentityAsync(selfId, CancellationToken.None);

        // ASSERT
        var activeIdentity = _activeIdentityContext.Identity;
        activeIdentity.Should().NotBeNull();
        activeIdentity!.SelfIdentityId.Should().Be(selfId);
        activeIdentity.PeerId.Should().Be(peerId);
        activeIdentity.ListeningPort.Should().Be(listeningPort);
        _activeIdentityContext.Keys.Should().NotBeNull();
        _activeIdentityContext.Keys!.IdentitySigningKey.Should().NotBeNull();
        _activeIdentityContext.Keys.SignedPreKey.Should().NotBeNull();
    }

    [Test]
    public void ResolveIdentityAsync_WhenIdentityNotFound_ThrowsInvalidOperationException()
    {
        // ARRANGE
        var selfId = new SelfId(1);

        _selfIdentityRepositoryMock
            .Setup(r => r.GetByIdAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SelfIdentity?)null);

        // ACT & ASSERT
        Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ResolveIdentityAsync(selfId, CancellationToken.None));
    }

    [Test]
    public async Task ResolveIdentityAsync_WhenKeysNotExists_GeneratesAndSavesNewKeys()
    {
        // ARRANGE
        var selfId = new SelfId(1);
        var peerId = new PeerId(Guid.NewGuid());
        var listeningPort = new ListeningPort(5000);
        var domainIdentity = new SelfIdentity(selfId, peerId, listeningPort);
        domainIdentity.SetDisplayName("TestIdentity");

        _selfIdentityRepositoryMock
            .Setup(r => r.GetByIdAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(domainIdentity);

        _keysStoreMock
            .Setup(k => k.LoadAsync(selfId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((X3dhKeys?)null);

        // ACT
        await _sut.ResolveIdentityAsync(selfId, CancellationToken.None);

        // ASSERT
        _keysStoreMock.Verify(
            k => k.SaveAsync(
                selfId,
                It.Is<X3dhKeys>(keys =>
                    keys.IdentitySigningKey != null &&
                    keys.SignedPreKey != null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
