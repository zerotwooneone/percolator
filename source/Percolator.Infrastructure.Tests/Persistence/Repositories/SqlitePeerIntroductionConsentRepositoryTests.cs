namespace Percolator.Infrastructure.Tests.Persistence.Repositories;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Serialization;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;

[TestFixture]
public sealed class SqlitePeerIntroductionConsentRepositoryTests
{
    private SqliteTestDb _db = null!;
    private ProtobufPayloadSerializer _serializer = null!;
    private SqlPeerIntroductionConsentRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
        _serializer = new ProtobufPayloadSerializer();
        _repo = new SqlPeerIntroductionConsentRepository(_db.DbContext, _serializer);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public async Task AddAsync_And_GetByIdAsync_RehydratesCompleteConsentRecordWithInvitation()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var channelId = ChannelId.New();
        var localId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var candidateId = PublicIdentityId.New();
        var forwardChannelId = ChannelId.New();
        var now = DateTimeOffset.UtcNow;

        var invitation = PeerInvitation.Create(
            remoteId,
            IdentityKey.FromBytes(new byte[32]),
            RelayEndpoint.Create("percolator://relay.example.com:443").Value!,
            BlindedRoutingToken.New()).Value;

        var consent = new PeerIntroductionConsent(
            requestId,
            channelId,
            localId,
            remoteId,
            candidateId,
            "Charlie",
            "Please meet Charlie",
            IntroductionDirection.OutboundRequest,
            IntroductionStatus.Pending,
            now,
            forwardChannelId: forwardChannelId,
            invitation: invitation);

        await _repo.AddAsync(consent);

        // Rehydrate using fresh DbContext to verify SQLite persistence durability
        using var newContext = _db.CreateNewContext();
        var freshRepo = new SqlPeerIntroductionConsentRepository(newContext, _serializer);

        var retrieved = await freshRepo.GetByIdAsync(requestId);
        retrieved.Should().NotBeNull();
        retrieved!.RequestId.Should().Be(requestId);
        retrieved.ChannelId.Should().Be(channelId);
        retrieved.LocalIdentityId.Should().Be(localId);
        retrieved.RemotePeerId.Should().Be(remoteId);
        retrieved.CandidatePeerId.Should().Be(candidateId);
        retrieved.CandidateDisplayName.Should().Be("Charlie");
        retrieved.Note.Should().Be("Please meet Charlie");
        retrieved.Direction.Should().Be(IntroductionDirection.OutboundRequest);
        retrieved.Status.Should().Be(IntroductionStatus.Pending);
        retrieved.ForwardChannelId.Should().Be(forwardChannelId);
        retrieved.Invitation.Should().NotBeNull();
        retrieved.Invitation!.InviterIdentityId.Should().Be(remoteId);
        retrieved.Invitation.RelayEndpoint.Value.Should().Be("percolator://relay.example.com:443");
    }

    [Test]
    public async Task UpdateAsync_StateTransition_Allowed_PersistsToSqlite()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var channelId = ChannelId.New();
        var localId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var candidateId = PublicIdentityId.New();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var consent = new PeerIntroductionConsent(
            requestId,
            channelId,
            localId,
            remoteId,
            candidateId,
            "Charlie",
            null,
            IntroductionDirection.InboundRequest,
            IntroductionStatus.Pending,
            createdAt);

        await _repo.AddAsync(consent);

        var invitation = PeerInvitation.Create(
            localId,
            IdentityKey.FromBytes(new byte[32]),
            RelayEndpoint.Create("percolator://relay.example.com:443").Value!,
            BlindedRoutingToken.New()).Value;

        var respondedAt = DateTimeOffset.UtcNow;
        consent.Allow(invitation, respondedAt).IsSuccess.Should().BeTrue();

        await _repo.UpdateAsync(consent);

        // Verify across DbContext boundary
        using var newContext = _db.CreateNewContext();
        var freshRepo = new SqlPeerIntroductionConsentRepository(newContext, _serializer);

        var retrieved = await freshRepo.GetByIdAsync(requestId);
        retrieved.Should().NotBeNull();
        retrieved!.Status.Should().Be(IntroductionStatus.Allowed);
        retrieved.Invitation.Should().NotBeNull();
        retrieved.Invitation!.InviterIdentityId.Should().Be(localId);
        retrieved.RespondedAtUtc.Should().NotBeNull();
    }

    [Test]
    public async Task UpdateAsync_StateTransition_Declined_PersistsToSqlite()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var channelId = ChannelId.New();
        var localId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var candidateId = PublicIdentityId.New();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var consent = new PeerIntroductionConsent(
            requestId,
            channelId,
            localId,
            remoteId,
            candidateId,
            "Charlie",
            null,
            IntroductionDirection.InboundRequest,
            IntroductionStatus.Pending,
            createdAt);

        await _repo.AddAsync(consent);

        var respondedAt = DateTimeOffset.UtcNow;
        consent.Decline("Too busy", respondedAt).IsSuccess.Should().BeTrue();

        await _repo.UpdateAsync(consent);

        using var newContext = _db.CreateNewContext();
        var freshRepo = new SqlPeerIntroductionConsentRepository(newContext, _serializer);

        var retrieved = await freshRepo.GetByIdAsync(requestId);
        retrieved.Should().NotBeNull();
        retrieved!.Status.Should().Be(IntroductionStatus.Declined);
        retrieved.DeclineReason.Should().Be("Too busy");
        retrieved.Invitation.Should().BeNull();
    }

    [Test]
    public async Task GetPendingInboundRequestsAsync_FiltersByLocalIdentityAndPendingStatus()
    {
        var localId1 = PublicIdentityId.New();
        var localId2 = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var candidateId = PublicIdentityId.New();

        // 1. Pending Inbound for localId1 -> Match
        var consent1 = new PeerIntroductionConsent(
            "req-1", ChannelId.New(), localId1, remoteId, candidateId, "Charlie", null,
            IntroductionDirection.InboundRequest, IntroductionStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-3));

        // 2. Allowed Inbound for localId1 -> No match (not pending)
        var consent2 = new PeerIntroductionConsent(
            "req-2", ChannelId.New(), localId1, remoteId, candidateId, "Charlie", null,
            IntroductionDirection.InboundRequest, IntroductionStatus.Allowed, DateTimeOffset.UtcNow.AddMinutes(-2));

        // 3. Pending Outbound for localId1 -> No match (outbound)
        var consent3 = new PeerIntroductionConsent(
            "req-3", ChannelId.New(), localId1, remoteId, candidateId, "Charlie", null,
            IntroductionDirection.OutboundRequest, IntroductionStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-1));

        // 4. Pending Inbound for localId2 -> No match (different owner)
        var consent4 = new PeerIntroductionConsent(
            "req-4", ChannelId.New(), localId2, remoteId, candidateId, "Charlie", null,
            IntroductionDirection.InboundRequest, IntroductionStatus.Pending, DateTimeOffset.UtcNow);

        await _repo.AddAsync(consent1);
        await _repo.AddAsync(consent2);
        await _repo.AddAsync(consent3);
        await _repo.AddAsync(consent4);

        var results = await _repo.GetPendingInboundRequestsAsync(localId1);
        results.Should().ContainSingle(c => c.RequestId == "req-1");
    }

    [Test]
    public async Task GetAllAsync_ReturnsAllRecordsForOwner_SortedByCreatedAt()
    {
        var localId = PublicIdentityId.New();
        var otherId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var candidateId = PublicIdentityId.New();

        var consent1 = new PeerIntroductionConsent(
            "req-early", ChannelId.New(), localId, remoteId, candidateId, "Alice", null,
            IntroductionDirection.InboundRequest, IntroductionStatus.Pending, DateTimeOffset.UtcNow.AddHours(-2));

        var consent2 = new PeerIntroductionConsent(
            "req-late", ChannelId.New(), localId, remoteId, candidateId, "Bob", null,
            IntroductionDirection.OutboundRequest, IntroductionStatus.Allowed, DateTimeOffset.UtcNow.AddHours(-1));

        var consentOther = new PeerIntroductionConsent(
            "req-other", ChannelId.New(), otherId, remoteId, candidateId, "Charlie", null,
            IntroductionDirection.InboundRequest, IntroductionStatus.Pending, DateTimeOffset.UtcNow);

        await _repo.AddAsync(consent1);
        await _repo.AddAsync(consent2);
        await _repo.AddAsync(consentOther);

        var results = await _repo.GetAllAsync(localId);
        results.Should().HaveCount(2);
        results[0].RequestId.Should().Be("req-late");
        results[1].RequestId.Should().Be("req-early");
    }
}
