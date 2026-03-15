using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Wpf.Features.Self;
using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Sessions.Models;
using Desktop.Wpf.Features.Sessions.State;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Percolator.Application.Cryptography;
using Percolator.Application.Network;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Identity;
using Percolator.Identity.Model;
using PeerId = Percolator.Network.PeerId;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class SecureChannelsProjectionTests
{
    [Test]
    public async Task PendingInboundCount_updates_when_pending_handshakes_exist()
    {
        // ARRANGE
        WpfTestHarness.EnsureApplication();

        var store = new SecureChannelsStore();

        var sessions = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessions
            .Setup(s => s.GetAllActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SecureSession>());

        var peers = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);

        var pendingQueries = new Mock<IPendingHandshakeQueries>(MockBehavior.Loose);
        pendingQueries
            .Setup(q => q.EnumerateOpenAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableFrom(new PendingHandshake
            {
                Id = PendingSessionId.NewId(),
                RemotePeer = new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid()),
                PeerName = "Alice",
                RequestCorrelationId = new RequestCorrelationId(Guid.NewGuid()),
                InviterFingerprintHex = null,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = null,
                IsRelayed = false,
                RelayPeer = null,
                RelayPeerName = null,
                RelayEndpoint = null
            }));

        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        preHandshake
            .Setup(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableFrom<PreHandshakeRecord>());

        var self = new SelfIdentityModel();
        self.Id.Value = "1";

        using var sut = new SecureChannelsProjection(
            store,
            sessions.Object,
            peers.Object,
            pendingQueries.Object,
            preHandshake.Object,
            self);

        // ACT
        await sut.Handle(new PendingSessionCreatedNotification(PendingSessionId.NewId()), CancellationToken.None);

        // ASSERT
        await WaitUntilAsync(
            predicate: () => store.PendingInboundCount.CurrentValue == 1 && store.Channels.Count > 0,
            timeout: TimeSpan.FromSeconds(2));

        store.PendingInboundCount.CurrentValue.Should().Be(1);
        store.PendingInbound.Count.Should().Be(1);
        store.Channels.Count.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task OutboundPending_migrates_to_active_session_without_creating_a_new_channel_model()
    {
        // ARRANGE
        WpfTestHarness.EnsureApplication();

        var store = new SecureChannelsStore();

        var peerId = new Percolator.Cryptography.Primitives.PeerId(Guid.NewGuid());
        var sessionId = SessionId.NewId();
        var corrId = Guid.NewGuid();

        var spki = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var now = DateTimeOffset.UtcNow;
        var peer = new PeerIdentity(new Percolator.Identity.PeerId(peerId.Value));
        peer.SetDisplayName("Alice");
        peer.AddKey(spki, now.AddMinutes(-10), now.AddMinutes(10), now);

        var recipientPkh = peer.GetActiveKey(now)!.Fingerprint;

        var sessions = new Mock<ISessionRepository>(MockBehavior.Loose);
        sessions
            .SetupSequence(s => s.GetAllActiveAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SecureSession>())
            .ReturnsAsync(new[]
            {
                RatchetBootstrap.CreateInitiatorSession(
                    sessionId,
                    peerId,
                    new ProtocolVersion(1),
                    new RootKey(new byte[32]),
                    new StaticClock(now))
            });

        var peers = new Mock<IPeerIdentityRepository>(MockBehavior.Loose);
        peers
            .Setup(p => p.GetByIdAsync(It.IsAny<Percolator.Identity.PeerId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(peer);
        peers
            .Setup(p => p.FindByPublicKeyHashAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(peer);

        var pendingQueries = new Mock<IPendingHandshakeQueries>(MockBehavior.Loose);
        pendingQueries
            .Setup(q => q.EnumerateOpenAsync(It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableFrom<PendingHandshake>());

        var record = new PreHandshakeRecord(
            Id: 1,
            SelfIdentityId: 1,
            RecipientPublicKeyHash: recipientPkh,
            LocalRequestId: corrId,
            InitiatorEphemeralPrivateKey: new byte[32],
            InitialRootKey: new byte[32],
            CreatedAtUtc: now,
            ExpiresAtUtc: null,
            RemoteIdentityKeySpki: spki);

        var preHandshake = new Mock<IPreHandshakeSessionStore>(MockBehavior.Loose);
        preHandshake
            .SetupSequence(s => s.EnumeratePendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableFrom(record))
            .Returns(AsyncEnumerableFrom(record));

        var self = new SelfIdentityModel();
        self.Id.Value = "1";

        using var sut = new SecureChannelsProjection(
            store,
            sessions.Object,
            peers.Object,
            pendingQueries.Object,
            preHandshake.Object,
            self);

        // ACT (first reload: outbound pending only)
        await sut.Handle(new PendingSessionCreatedNotification(PendingSessionId.NewId()), CancellationToken.None);

        await WaitUntilAsync(
            predicate: () => store.Channels.Count == 1,
            timeout: TimeSpan.FromSeconds(2));

        var firstModel = store.Channels.Single();
        firstModel.Key.Should().Be(SecureChannelKey.FromPendingCorrelationId(corrId));
        firstModel.Kind.CurrentValue.Should().Be(Desktop.Wpf.Features.Sessions.Models.SecureChannelKind.PendingOutbound);

        // ACT (second reload: session exists, correlation possible => migration)
        await sut.Handle(new SecureSessionCreatedNotification(sessionId, SecureSessionCreatedReason.InitiatorFinalize, peerId, new ProtocolVersion(1)), CancellationToken.None);

        await WaitUntilAsync(
            predicate: () => store.Channels.Count == 1 && store.Channels.Single().Key.Equals(SecureChannelKey.FromSessionId(sessionId.Value)),
            timeout: TimeSpan.FromSeconds(2));

        // ASSERT
        var migrated = store.Channels.Single();
        ReferenceEquals(firstModel, migrated).Should().BeTrue("migration should preserve object identity for UI continuity");
        migrated.Key.Should().Be(SecureChannelKey.FromSessionId(sessionId.Value));
        migrated.Kind.CurrentValue.Should().Be(Desktop.Wpf.Features.Sessions.Models.SecureChannelKind.Direct);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate() && !cts.IsCancellationRequested)
        {
            WpfTestHarness.DoEvents();
            await Task.Delay(10, cts.Token);
        }

        if (!predicate())
        {
            throw new TimeoutException("Condition not met within timeout.");
        }
    }

    private static async IAsyncEnumerable<T> AsyncEnumerableFrom<T>(params T[] items)
    {
        foreach (var it in items)
        {
            yield return it;
            await Task.Yield();
        }
    }

    private sealed class StaticClock : IClock
    {
        public StaticClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }
}
