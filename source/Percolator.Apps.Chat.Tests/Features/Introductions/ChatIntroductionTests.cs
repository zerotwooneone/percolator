namespace Percolator.Apps.Chat.Tests.Features.Introductions;

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Introductions;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Ingress;
using Percolator.Apps.Chat.Tests.TestDoubles;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

[TestFixture]
public sealed class ChatIntroductionTests
{
    private InMemoryChatMessageRepository _chatRepo = null!;
    private InMemoryChannelRepository _channelRepo = null!;
    private InMemoryGroupInvitationRepository _invitationRepo = null!;
    private InMemoryUnknownGroupCache _unknownGroupCache = null!;
    private TestPayloadSender _payloadSender = null!;
    private TestPayloadSerializer _serializer = null!;
    private FakeDateTimeProvider _timeProvider = null!;
    private InMemoryPeerIntroductionConsentRepository _introConsentRepo = null!;
    private FakePeerInvitationService _invitationService = null!;
    private InMemoryPeerContactRepository _contactRepo = null!;
    private FakePeerConnectionService _peerConnectionService = null!;
    private RecordingEventPublisher _eventPublisher = null!;
    private ChatService _chatService = null!;
    private ChatPayloadHandler _payloadHandler = null!;

    private readonly RelayEndpoint _relayEndpoint = RelayEndpoint.Create("percolator://relay.example.com:443").Value!;

    [SetUp]
    public void SetUp()
    {
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _invitationRepo = new InMemoryGroupInvitationRepository();
        _unknownGroupCache = new InMemoryUnknownGroupCache();
        _payloadSender = new TestPayloadSender();
        _serializer = new TestPayloadSerializer();
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        _introConsentRepo = new InMemoryPeerIntroductionConsentRepository();
        _invitationService = new FakePeerInvitationService();
        _contactRepo = new InMemoryPeerContactRepository();
        _peerConnectionService = new FakePeerConnectionService();
        _eventPublisher = new RecordingEventPublisher();

        _chatService = new ChatService(
            _chatRepo,
            _channelRepo,
            _payloadSender,
            _serializer,
            _timeProvider,
            new FakeBlobStorageService(),
            _introConsentRepo,
            _invitationService,
            _contactRepo,
            _peerConnectionService);

        _payloadHandler = new ChatPayloadHandler(
            _chatRepo,
            _channelRepo,
            _invitationRepo,
            _serializer,
            _timeProvider,
            _payloadSender,
            _unknownGroupCache,
            blobStorage: new FakeBlobStorageService(),
            new InMemoryPeerProfileRepository(),
            new FakeProfileDeliveryClient(),
            new FakeProfileManager(),
            new ChatTestCryptoEngine(),
            new FakePeerMailboxRoutingRepository(),
            _eventPublisher,
            _introConsentRepo,
            _contactRepo);
    }

    [Test]
    public async Task RequestIntroductionConsentAsync_HappyPath_StagesOutboundPendingConsent_AndDispatchesPayload()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channel = DirectChannel.Create(
            ChannelId.New(), alice, bob, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channel);

        var forwardChannel = DirectChannel.Create(
            ChannelId.New(), alice, charlie, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(forwardChannel);

        var cmd = new RequestIntroductionConsentCommand(
            channel.Id,
            RequesterId: alice,
            CandidatePeerId: charlie,
            CandidateDisplayName: "Charlie",
            ForwardChannelId: forwardChannel.Id,
            Note: "Bob, please meet Charlie!");

        var result = await _chatService.RequestIntroductionConsentAsync(cmd);

        result.IsSuccess.Should().BeTrue();
        string requestId = result.Value;
        requestId.Should().NotBeNullOrWhiteSpace();

        // Verify staged in repository
        var staged = await _introConsentRepo.GetByIdAsync(requestId);
        staged.Should().NotBeNull();
        staged!.Direction.Should().Be(IntroductionDirection.OutboundRequest);
        staged.Status.Should().Be(IntroductionStatus.Pending);
        staged.LocalIdentityId.Should().Be(alice);
        staged.RemotePeerId.Should().Be(bob);
        staged.CandidatePeerId.Should().Be(charlie);
        staged.ForwardChannelId.Should().Be(forwardChannel.Id);

        // Verify outbound payload dispatched
        _payloadSender.SentPayloads.Should().ContainSingle(p => p.ChannelId == channel.Id);
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(_payloadSender.SentPayloads[0].Payload).Value!;
        envelope.Type.Should().Be(ChatPayloadType.IntroductionConsentRequest);
    }

    [Test]
    public async Task RequestIntroductionConsentAsync_CandidateIsBlocked_FailsFast()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channel = DirectChannel.Create(
            ChannelId.New(), alice, bob, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channel);

        // Charlie is blocked by Alice
        var blockedContact = new PeerContact(
            alice, charlie, ContactNickname.Create("Charlie", charlie), PeerTrustLevel.Blocked, _timeProvider.UtcNow);
        await _contactRepo.SaveAsync(blockedContact);

        var cmd = new RequestIntroductionConsentCommand(
            channel.Id,
            RequesterId: alice,
            CandidatePeerId: charlie,
            CandidateDisplayName: "Charlie");

        var result = await _chatService.RequestIntroductionConsentAsync(cmd);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("CANDIDATE_BLOCKED");
        _payloadSender.SentPayloads.Should().BeEmpty();
    }

    [Test]
    public async Task HandleIntroductionConsentRequest_Inbound_StagesInboundPendingConsent_AndPublishesEvent()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channel = DirectChannel.Create(
            ChannelId.New(), bob, alice, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channel);

        var requestId = Guid.NewGuid().ToString("N");
        var reqDto = new IntroductionConsentRequestDto(
            requestId,
            charlie,
            "Charlie",
            "Hey Bob, meet Charlie!",
            (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds());

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.IntroductionConsentRequest, reqDto);
        var inboundContext = new InboundPayloadContext(
            channel.Id,
            RecipientIdentityId: bob,
            SenderIdentityId: alice,
            DeviceId.Primary,
            AppId.Chat,
            envelopeBytes,
            _timeProvider.UtcNow);

        var result = await _payloadHandler.HandleInboundAsync(inboundContext);

        result.IsSuccess.Should().BeTrue();

        // Verify staged in repo
        var staged = await _introConsentRepo.GetByIdAsync(requestId);
        staged.Should().NotBeNull();
        staged!.Direction.Should().Be(IntroductionDirection.InboundRequest);
        staged.Status.Should().Be(IntroductionStatus.Pending);
        staged.LocalIdentityId.Should().Be(bob);
        staged.RemotePeerId.Should().Be(alice);
        staged.CandidatePeerId.Should().Be(charlie);

        // Verify application event published
        _eventPublisher.PublishedEvents.Should().ContainSingle(e => e is IntroductionConsentRequestedApplicationEvent);
        var appEvent = (IntroductionConsentRequestedApplicationEvent)_eventPublisher.PublishedEvents[0];
        appEvent.RequestId.Should().Be(requestId);
        appEvent.RequesterPeerId.Should().Be(alice);
        appEvent.CandidatePeerId.Should().Be(charlie);
    }

    [Test]
    public async Task RespondToIntroductionConsentAsync_Allowed_CreatesInvitation_TransitionsState_AndSendsResponse()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channel = DirectChannel.Create(
            ChannelId.New(), bob, alice, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channel);

        var requestId = Guid.NewGuid().ToString("N");
        var consent = new PeerIntroductionConsent(
            requestId,
            channel.Id,
            bob,
            alice,
            charlie,
            "Charlie",
            null,
            IntroductionDirection.InboundRequest,
            IntroductionStatus.Pending,
            _timeProvider.UtcNow);
        await _introConsentRepo.AddAsync(consent);

        var cmd = new RespondToIntroductionConsentCommand(requestId, Allowed: true);
        var result = await _chatService.RespondToIntroductionConsentAsync(cmd);

        result.IsSuccess.Should().BeTrue();

        // Verify state transition
        var updated = await _introConsentRepo.GetByIdAsync(requestId);
        updated!.Status.Should().Be(IntroductionStatus.Allowed);
        updated.Invitation.Should().NotBeNull();
        updated.Invitation!.InviterIdentityId.Should().Be(bob);

        // Verify response payload sent
        _payloadSender.SentPayloads.Should().ContainSingle(p => p.ChannelId == channel.Id);
        var envelope = _serializer.Deserialize<ChatPayloadEnvelopeDto>(_payloadSender.SentPayloads[0].Payload).Value!;
        envelope.Type.Should().Be(ChatPayloadType.IntroductionConsentResponse);

        var respDto = _serializer.Deserialize<IntroductionConsentResponseDto>(envelope.Data).Value!;
        respDto.Allowed.Should().BeTrue();
        respDto.Invitation.Should().NotBeNull();
        respDto.Invitation!.InviterIdentityId.Should().Be(bob);
    }

    [Test]
    public async Task HandleIntroductionConsentResponse_Allowed_AutoForwardsCardToCharlie_IfForwardChannelSet()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channelWithBob = DirectChannel.Create(
            ChannelId.New(), alice, bob, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channelWithBob);

        var channelWithCharlie = DirectChannel.Create(
            ChannelId.New(), alice, charlie, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channelWithCharlie);

        var requestId = Guid.NewGuid().ToString("N");
        var outboundConsent = new PeerIntroductionConsent(
            requestId,
            channelWithBob.Id,
            alice,
            bob,
            charlie,
            "Bob",
            "Meet Bob!",
            IntroductionDirection.OutboundRequest,
            IntroductionStatus.Pending,
            _timeProvider.UtcNow,
            forwardChannelId: channelWithCharlie.Id);
        await _introConsentRepo.AddAsync(outboundConsent);

        var bobInvitation = PeerInvitation.Create(
            bob,
            IdentityKey.FromBytes(new byte[32]),
            _relayEndpoint,
            BlindedRoutingToken.New()).Value!;

        var responseDto = new IntroductionConsentResponseDto(
            requestId,
            Allowed: true,
            PeerInvitationWireDto.FromDomain(bobInvitation),
            DeclineReason: null,
            (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds());

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.IntroductionConsentResponse, responseDto);
        var inboundContext = new InboundPayloadContext(
            channelWithBob.Id,
            RecipientIdentityId: alice,
            SenderIdentityId: bob,
            DeviceId.Primary,
            AppId.Chat,
            envelopeBytes,
            _timeProvider.UtcNow);

        var result = await _payloadHandler.HandleInboundAsync(inboundContext);

        result.IsSuccess.Should().BeTrue();

        // Verify status marked Forwarded
        var updated = await _introConsentRepo.GetByIdAsync(requestId);
        updated!.Status.Should().Be(IntroductionStatus.Forwarded);

        // Verify PeerIntroductionCard was forwarded to Charlie on channelWithCharlie
        _payloadSender.SentPayloads.Should().ContainSingle(p => p.ChannelId == channelWithCharlie.Id);
        var cardEnv = _serializer.Deserialize<ChatPayloadEnvelopeDto>(_payloadSender.SentPayloads[0].Payload).Value!;
        cardEnv.Type.Should().Be(ChatPayloadType.PeerIntroductionCard);

        var cardDto = _serializer.Deserialize<PeerIntroductionCardDto>(cardEnv.Data).Value!;
        cardDto.IntroducedPeerId.Should().Be(bob);
        cardDto.Invitation.InviterIdentityId.Should().Be(bob);

        // Verify IntroductionConsentResolvedApplicationEvent published
        _eventPublisher.PublishedEvents.Should().ContainSingle(e => e is IntroductionConsentResolvedApplicationEvent);
    }

    [Test]
    public async Task HandlePeerIntroductionCard_InboundOnCharlie_EnforcesSameExchangeAndStagesCard()
    {
        var alice = PublicIdentityId.New();
        var bob = PublicIdentityId.New();
        var charlie = PublicIdentityId.New();

        var channelWithAlice = DirectChannel.Create(
            ChannelId.New(), charlie, alice, _timeProvider, relayEndpoint: _relayEndpoint).Value!;
        await _channelRepo.SaveDirectAsync(channelWithAlice);

        var bobInvitation = PeerInvitation.Create(
            bob,
            IdentityKey.FromBytes(new byte[32]),
            _relayEndpoint,
            BlindedRoutingToken.New()).Value!;

        var cardDto = new PeerIntroductionCardDto(
            "intro-card-1",
            bob,
            "Bob",
            PeerInvitationWireDto.FromDomain(bobInvitation),
            "Here is Bob's contact info",
            (ulong)_timeProvider.UtcNow.ToUnixTimeMilliseconds());

        var envelopeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(_serializer, ChatPayloadType.PeerIntroductionCard, cardDto);
        var inboundContext = new InboundPayloadContext(
            channelWithAlice.Id,
            RecipientIdentityId: charlie,
            SenderIdentityId: alice,
            DeviceId.Primary,
            AppId.Chat,
            envelopeBytes,
            _timeProvider.UtcNow);

        var result = await _payloadHandler.HandleInboundAsync(inboundContext);

        result.IsSuccess.Should().BeTrue();

        var stagedCard = await _introConsentRepo.GetByIdAsync("intro-card-1");
        stagedCard.Should().NotBeNull();
        stagedCard!.Direction.Should().Be(IntroductionDirection.InboundCard);
        stagedCard.Status.Should().Be(IntroductionStatus.Pending);
        stagedCard.CandidatePeerId.Should().Be(bob);
        stagedCard.Invitation.Should().NotBeNull();

        _eventPublisher.PublishedEvents.Should().ContainSingle(e => e is PeerIntroductionCardReceivedApplicationEvent);
    }

    [Test]
    public async Task AcceptPeerIntroductionCardAsync_ConnectsToIntroducedPeer_AndMarksConnected()
    {
        var charlie = PublicIdentityId.New();
        var bob = PublicIdentityId.New();

        var bobInvitation = PeerInvitation.Create(
            bob,
            IdentityKey.FromBytes(new byte[32]),
            _relayEndpoint,
            BlindedRoutingToken.New()).Value!;

        var cardConsent = new PeerIntroductionConsent(
            "card-to-accept",
            ChannelId.New(),
            charlie,
            PublicIdentityId.New(),
            bob,
            "Bob",
            "Here is Bob",
            IntroductionDirection.InboundCard,
            IntroductionStatus.Pending,
            _timeProvider.UtcNow,
            invitation: bobInvitation);
        await _introConsentRepo.AddAsync(cardConsent);

        var cmd = new AcceptPeerIntroductionCardCommand("card-to-accept", charlie);
        var result = await _chatService.AcceptPeerIntroductionCardAsync(cmd);

        result.IsSuccess.Should().BeTrue();

        _peerConnectionService.LastReceivedInvitation.Should().NotBeNull();
        _peerConnectionService.LastReceivedInvitation!.InviterIdentityId.Should().Be(bob);

        var updated = await _introConsentRepo.GetByIdAsync("card-to-accept");
        updated!.Status.Should().Be(IntroductionStatus.Connected);
    }

    [Test]
    public async Task AcceptPeerIntroductionCardAsync_IdentityKeyChanged_FailsWithSysKeyChangeHalt()
    {
        var charlie = PublicIdentityId.New();
        var bob = PublicIdentityId.New();

        var originalKeyBytes = new byte[32];
        originalKeyBytes[0] = 0xAA;
        var originalKey = IdentityKey.FromBytes(originalKeyBytes);

        // Charlie already had Bob pinned with originalKey
        var pinnedContact = new PeerContact(
            charlie, bob, ContactNickname.Create("Bob", bob), PeerTrustLevel.Verified, _timeProvider.UtcNow,
            primaryPublicKey: originalKey);
        await _contactRepo.SaveAsync(pinnedContact);

        // But the invitation has a different key!
        var differentKeyBytes = new byte[32];
        differentKeyBytes[0] = 0xFF;
        var differentKey = IdentityKey.FromBytes(differentKeyBytes);

        var bobInvitation = PeerInvitation.Create(
            bob,
            differentKey,
            _relayEndpoint,
            BlindedRoutingToken.New()).Value!;

        var cardConsent = new PeerIntroductionConsent(
            "card-tampered-key",
            ChannelId.New(),
            charlie,
            PublicIdentityId.New(),
            bob,
            "Bob",
            "Meet Bob",
            IntroductionDirection.InboundCard,
            IntroductionStatus.Pending,
            _timeProvider.UtcNow,
            invitation: bobInvitation);
        await _introConsentRepo.AddAsync(cardConsent);

        var cmd = new AcceptPeerIntroductionCardCommand("card-tampered-key", charlie);
        var result = await _chatService.AcceptPeerIntroductionCardAsync(cmd);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("IDENTITY_KEY_CHANGED");
        _peerConnectionService.LastReceivedInvitation.Should().BeNull();
    }
}
