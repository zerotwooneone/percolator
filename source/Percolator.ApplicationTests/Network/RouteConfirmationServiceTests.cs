using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Percolator.Identity;
using Percolator.Network;
using Percolator.Network.Messaging;
using Percolator.Network.ValueObjects;
using Percolator.Application.Network;

namespace Percolator.ApplicationTests.Network;

[TestFixture]
public class RouteConfirmationServiceTests
{
    [Test]
    public async Task PromoteToConfirmedAsync_DirectEndpoint_AddsToProfile()
    {
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var profileRepo = new Mock<IPeerRoutingProfileRepository>();
        var logger = new Mock<ILogger<RouteConfirmationService>>();

        var selfIdentityId = new SelfId(1);
        var remotePeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var endpointHost = "example.com";
        var endpointPort = 443;
        var nowUtc = DateTimeOffset.UtcNow;

        var existingProfile = new PeerRoutingProfile();
        existingProfile.BindIdentity(remotePeerId);
        profileRepo.Setup(r => r.GetByIdAsync(remotePeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProfile);

        var sut = new RouteConfirmationService(candidateRepo.Object, profileRepo.Object, logger.Object);

        await sut.PromoteToConfirmedAsync(
            selfIdentityId,
            remotePeerId,
            RouteKind.Direct,
            endpointHost,
            endpointPort,
            relayHostPeerId: null,
            nowUtc,
            CancellationToken.None);

        profileRepo.Verify(r => r.UpsertAsync(
            It.Is<PeerRoutingProfile>(p =>
                p.Id == remotePeerId &&
                p.Endpoints.Any(e => e.EndPoint.Host == endpointHost && e.EndPoint.Port == endpointPort)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PromoteToConfirmedAsync_RelayHost_AddsRelayLink()
    {
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var profileRepo = new Mock<IPeerRoutingProfileRepository>();
        var logger = new Mock<ILogger<RouteConfirmationService>>();

        var selfIdentityId = new SelfId(1);
        var remotePeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var relayHostPeerId = Guid.NewGuid();
        var nowUtc = DateTimeOffset.UtcNow;

        var existingProfile = new PeerRoutingProfile();
        existingProfile.BindIdentity(remotePeerId);
        profileRepo.Setup(r => r.GetByIdAsync(remotePeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingProfile);

        var sut = new RouteConfirmationService(candidateRepo.Object, profileRepo.Object, logger.Object);

        await sut.PromoteToConfirmedAsync(
            selfIdentityId,
            remotePeerId,
            RouteKind.Relayed,
            endpointHost: null,
            endpointPort: null,
            relayHostPeerId,
            nowUtc,
            CancellationToken.None);

        profileRepo.Verify(r => r.UpsertAsync(
            It.Is<PeerRoutingProfile>(p =>
                p.Id == remotePeerId &&
                p.Relays.Any(r => r.RelayPeerId.Value == relayHostPeerId)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PromoteToConfirmedAsync_CreatesNewProfile_WhenNoneExists()
    {
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var profileRepo = new Mock<IPeerRoutingProfileRepository>();
        var logger = new Mock<ILogger<RouteConfirmationService>>();

        var selfIdentityId = new SelfId(1);
        var remotePeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var endpointHost = "example.com";
        var endpointPort = 443;
        var nowUtc = DateTimeOffset.UtcNow;

        profileRepo.Setup(r => r.GetByIdAsync(remotePeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PeerRoutingProfile?)null);

        var sut = new RouteConfirmationService(candidateRepo.Object, profileRepo.Object, logger.Object);

        await sut.PromoteToConfirmedAsync(
            selfIdentityId,
            remotePeerId,
            RouteKind.Direct,
            endpointHost,
            endpointPort,
            relayHostPeerId: null,
            nowUtc,
            CancellationToken.None);

        profileRepo.Verify(r => r.UpsertAsync(
            It.Is<PeerRoutingProfile>(p =>
                p.Id == remotePeerId &&
                p.Endpoints.Any(e => e.EndPoint.Host == endpointHost && e.EndPoint.Port == endpointPort)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RecordAttemptAsync_UpdatesCandidateStats_OnSuccess()
    {
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var profileRepo = new Mock<IPeerRoutingProfileRepository>();
        var logger = new Mock<ILogger<RouteConfirmationService>>();

        var selfIdentityId = new SelfId(1);
        var remotePeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var endpointHost = "example.com";
        var endpointPort = 443;
        var nowUtc = DateTimeOffset.UtcNow;

        var existingCandidate = new PeerRouteCandidate
        {
            Id = 1,
            SelfIdentityId = selfIdentityId.Value,
            RemotePeerId = remotePeerId,
            RouteKind = RouteKind.Direct,
            EndpointHost = endpointHost,
            EndpointPort = endpointPort,
            ObservedAtUtc = nowUtc.AddDays(-1),
            AttemptCount = 0
        };

        candidateRepo.Setup(r => r.GetCandidatesAsync(selfIdentityId.Value, remotePeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existingCandidate });

        var sut = new RouteConfirmationService(candidateRepo.Object, profileRepo.Object, logger.Object);

        await sut.RecordAttemptAsync(
            selfIdentityId,
            remotePeerId,
            RouteKind.Direct,
            endpointHost,
            endpointPort,
            relayHostPeerId: null,
            success: true,
            nowUtc,
            CancellationToken.None);

        candidateRepo.Verify(r => r.UpsertAsync(
            It.Is<PeerRouteCandidate>(c =>
                c.AttemptCount == 1 &&
                c.LastAttemptAtUtc == nowUtc &&
                c.LastSuccessAtUtc == nowUtc &&
                c.LastError == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RecordAttemptAsync_UpdatesCandidateStats_OnFailure()
    {
        var candidateRepo = new Mock<IPeerRouteCandidateRepository>();
        var profileRepo = new Mock<IPeerRoutingProfileRepository>();
        var logger = new Mock<ILogger<RouteConfirmationService>>();

        var selfIdentityId = new SelfId(1);
        var remotePeerId = new Percolator.Network.PeerId(Guid.NewGuid());
        var endpointHost = "example.com";
        var endpointPort = 443;
        var nowUtc = DateTimeOffset.UtcNow;

        var existingCandidate = new PeerRouteCandidate
        {
            Id = 1,
            SelfIdentityId = selfIdentityId.Value,
            RemotePeerId = remotePeerId,
            RouteKind = RouteKind.Direct,
            EndpointHost = endpointHost,
            EndpointPort = endpointPort,
            ObservedAtUtc = nowUtc.AddDays(-1),
            AttemptCount = 0
        };

        candidateRepo.Setup(r => r.GetCandidatesAsync(selfIdentityId.Value, remotePeerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { existingCandidate });

        var sut = new RouteConfirmationService(candidateRepo.Object, profileRepo.Object, logger.Object);

        await sut.RecordAttemptAsync(
            selfIdentityId,
            remotePeerId,
            RouteKind.Direct,
            endpointHost,
            endpointPort,
            relayHostPeerId: null,
            success: false,
            nowUtc,
            CancellationToken.None);

        candidateRepo.Verify(r => r.UpsertAsync(
            It.Is<PeerRouteCandidate>(c =>
                c.AttemptCount == 1 &&
                c.LastAttemptAtUtc == nowUtc &&
                c.LastSuccessAtUtc == null &&
                c.LastError == "Attempt failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
