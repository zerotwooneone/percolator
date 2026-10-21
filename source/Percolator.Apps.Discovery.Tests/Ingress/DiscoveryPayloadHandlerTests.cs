using System.Buffers;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Discovery.Features.NearbyPeerDiscovery;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Apps.Discovery.Features.RelayDirectoryHosting;
using Percolator.Apps.Discovery.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.PluginSdk;

namespace Percolator.Apps.Discovery.Tests.Ingress;

[TestFixture]
public sealed class DiscoveryPayloadHandlerTests
{
    private DiscoveryAdmissionGate _admissionGate = null!;
    private InMemoryRelayPeerDirectory _directory = null!;
    private PrivateRendezvousService _rendezvousService = null!;
    private FakeDiscoverySerializer _serializer = null!;
    private FakePayloadSender _payloadSender = null!;
    private DiscoveryPayloadHandler _handler = null!;

    private readonly ChannelId _channelId = ChannelId.New();
    private readonly PublicIdentityId _aliceId = PublicIdentityId.New();
    private readonly PublicIdentityId _bobId = PublicIdentityId.New();
    private readonly DeviceId _deviceId = new(1);

    [SetUp]
    public void SetUp()
    {
        _admissionGate = new DiscoveryAdmissionGate();
        _directory = new InMemoryRelayPeerDirectory(_admissionGate, RelayDiscoveryPolicy.Open);
        _rendezvousService = new PrivateRendezvousService();
        _serializer = new FakeDiscoverySerializer();
        _payloadSender = new FakePayloadSender();

        _handler = new DiscoveryPayloadHandler(_directory, _rendezvousService, _serializer, _payloadSender);
    }

    [Test]
    public async Task HandleRegistrationAsync_WhenSenderMatchesPeerId_Succeeds()
    {
        // First register Bob in directory
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(
            _bobId,
            PeerDiscoveryVisibility.Hidden,
            HasAuthenticatedHandshake: true,
            HasActiveSignedPreKey: true));

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.RegisterVisibility },
            DateTimeOffset.UtcNow);

        var payload = new DiscoveryRegistrationPayload(_bobId, PeerDiscoveryVisibility.Discoverable);
        var result = await _handler.HandleRegistrationAsync(context, payload);

        result.IsSuccess.Should().BeTrue();

        var updated = await _directory.GetPeerAsync(_bobId);
        updated.Should().NotBeNull();
        updated!.Visibility.Should().Be(PeerDiscoveryVisibility.Discoverable);
    }

    [Test]
    public async Task HandleRegistrationAsync_WhenSenderDoesNotMatchPeerId_RejectsWithUnauthorized()
    {
        // Alice attempts to alter Bob's visibility
        var context = new InboundPayloadContext(
            _channelId,
            _aliceId, // Authenticated session sender is Alice
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.RegisterVisibility },
            DateTimeOffset.UtcNow);

        var payload = new DiscoveryRegistrationPayload(_bobId, PeerDiscoveryVisibility.Hidden);
        var result = await _handler.HandleRegistrationAsync(context, payload);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("UNAUTHORIZED_REGISTRATION");
    }

    [Test]
    public async Task HandleNearbyQueryAsync_ReturnsCandidatesExcludingRequester()
    {
        var peerCharlie = PublicIdentityId.New();

        // Register Bob and Charlie (both discoverable)
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(_bobId, PeerDiscoveryVisibility.Discoverable, true, true));
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(peerCharlie, PeerDiscoveryVisibility.Discoverable, true, true));

        // Bob queries for nearby peers
        var context = new InboundPayloadContext(
            _channelId,
            _bobId, // Requester is Bob
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.NearbyQuery },
            DateTimeOffset.UtcNow);

        var query = new NearbyDiscoveryQueryPayload(_bobId, 50);
        var responseResult = await _handler.HandleNearbyQueryAsync(context, query);

        responseResult.IsSuccess.Should().BeTrue();
        // Response should contain Charlie, but NOT Bob (the requester)
        responseResult.Value.Descriptors.Should().ContainSingle(d => d.PeerId == peerCharlie);
        responseResult.Value.Descriptors.Should().NotContain(d => d.PeerId == _bobId);
    }

    [Test]
    public async Task HandleNearbyQueryAsync_WhenRelayDisabled_ReturnsFailure()
    {
        _directory.Policy = RelayDiscoveryPolicy.Disabled;

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.NearbyQuery },
            DateTimeOffset.UtcNow);

        var query = new NearbyDiscoveryQueryPayload(_bobId, 50);
        var responseResult = await _handler.HandleNearbyQueryAsync(context, query);

        responseResult.IsSuccess.Should().BeFalse();
        responseResult.Error.Code.Should().Be("RELAY_DISCOVERY_DISABLED");
    }

    [Test]
    public async Task HandleNearbyQueryAsync_WhenRelayBlindedContactsOnly_ProhibitsOpenDiscovery()
    {
        _directory.Policy = RelayDiscoveryPolicy.BlindedContactsOnly;

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.NearbyQuery },
            DateTimeOffset.UtcNow);

        var query = new NearbyDiscoveryQueryPayload(_bobId, 50);
        var responseResult = await _handler.HandleNearbyQueryAsync(context, query);

        responseResult.IsSuccess.Should().BeFalse();
        responseResult.Error.Code.Should().Be("OPEN_DISCOVERY_PROHIBITED");
    }

    [Test]
    public async Task HandleInboundAsync_DispatchesNearbyQueryAndEmitsResponseFrame()
    {
        var peerCharlie = PublicIdentityId.New();
        await _directory.RegisterPeerAsync(new RelayPeerRegistration(peerCharlie, PeerDiscoveryVisibility.Discoverable, true, true));

        var query = new NearbyDiscoveryQueryPayload(_bobId, 25);
        byte[] framedQuery = _serializer.Frame(DiscoveryOpcode.NearbyQuery, query);

        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            framedQuery,
            DateTimeOffset.UtcNow);

        var result = await _handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeTrue();
        _payloadSender.SentPayloads.Should().ContainSingle();
        var outbound = _payloadSender.SentPayloads[0];
        outbound.AppId.Should().Be(AppId.Discovery);
        outbound.RecipientIdentityId.Should().Be(_bobId);
        outbound.Payload.Span[0].Should().Be((byte)DiscoveryOpcode.NearbyResponse);
    }

    [Test]
    public void DiscoveryIngressFilter_EnforcesPolicyAndRateLimits()
    {
        var filter = new DiscoveryIngressFilter(_directory, maxRequestsPerWindow: 2);
        var validQueryFrame = new byte[] { (byte)DiscoveryOpcode.NearbyQuery, 0x01 };

        var ctx = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Discovery,
            validQueryFrame,
            DateTimeOffset.UtcNow);

        // 1. Allowed up to rate limit (2 requests)
        filter.Evaluate(ctx).Should().Be(IngressDecision.Accept);
        filter.Evaluate(ctx).Should().Be(IngressDecision.Accept);
        filter.Evaluate(ctx).Should().Be(IngressDecision.DiscardSilently);

        // 2. BlindedContactsOnly drops NearbyQuery but accepts RendezvousQuery for another peer
        _directory.Policy = RelayDiscoveryPolicy.BlindedContactsOnly;
        var aliceNearbyCtx = new InboundPayloadContext(
            _channelId,
            _aliceId,
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.NearbyQuery, 0x01 },
            DateTimeOffset.UtcNow);
        filter.Evaluate(aliceNearbyCtx).Should().Be(IngressDecision.DiscardSilently);

        var aliceRendezvousCtx = new InboundPayloadContext(
            _channelId,
            _aliceId,
            _deviceId,
            AppId.Discovery,
            new byte[] { (byte)DiscoveryOpcode.RendezvousQuery, 0x01 },
            DateTimeOffset.UtcNow);
        filter.Evaluate(aliceRendezvousCtx).Should().Be(IngressDecision.Accept);
    }

    [Test]
    public async Task HandleInboundAsync_AppIdMismatch_ReturnsFailure()
    {
        var context = new InboundPayloadContext(
            _channelId,
            _bobId,
            _deviceId,
            AppId.Chat, // Mismatched AppId
            new byte[] { 1 },
            DateTimeOffset.UtcNow);

        var result = await _handler.HandleInboundAsync(context);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("APP_ID_MISMATCH");
    }

    private sealed class FakeDiscoverySerializer : IPayloadSerializer
    {
        private readonly Dictionary<byte, object> _store = new();
        private byte _nextId = 1;

        public byte[] Frame<T>(DiscoveryOpcode opcode, T payload) where T : notnull
        {
            var body = Serialize(payload);
            var framed = new byte[1 + body.Length];
            framed[0] = (byte)opcode;
            body.Span.CopyTo(framed.AsSpan(1));
            return framed;
        }

        public void Serialize<T>(T payload, IBufferWriter<byte> writer)
        {
            var bytes = Serialize(payload);
            writer.Write(bytes.Span);
        }

        public ReadOnlyMemory<byte> Serialize<T>(T payload)
        {
            byte id = _nextId++;
            _store[id] = payload!;
            return new byte[] { id };
        }

        public DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data)
        {
            if (data.IsEmpty || !_store.TryGetValue(data.Span[0], out var obj) || obj is not T typed)
            {
                return DomainResult<T>.Failure(new DomainError("DESERIALIZE_ERROR", "Failed to deserialize test payload."));
            }

            return DomainResult<T>.Success(typed);
        }
    }

    private sealed class FakePayloadSender : IPayloadSender
    {
        public List<OutboundPayloadContext> SentPayloads { get; } = new();

        public ValueTask<DomainResult> SendPayloadAsync(OutboundPayloadContext context, CancellationToken ct = default)
        {
            SentPayloads.Add(context);
            return ValueTask.FromResult(DomainResult.Success());
        }
    }
}
