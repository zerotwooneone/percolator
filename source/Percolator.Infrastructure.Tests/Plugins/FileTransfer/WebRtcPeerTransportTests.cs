namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Events;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class WebRtcPeerTransportTests
{
    private SqliteTestDb _dbA = null!;
    private SqliteTestDb _dbB = null!;
    private PeerGrpcChannelFactory _channelFactory = null!;
    private InMemoryEventBus _eventBusA = null!;
    private InMemoryEventBus _eventBusB = null!;
    private TransferPeerTransport _transportA = null!;
    private TransferPeerTransport _transportB = null!;
    private UdpStunResponderService _stunService = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        _dbA = new SqliteTestDb();
        _dbB = new SqliteTestDb();
        var clock = new SystemDateTimeProvider();
        _channelFactory = new PeerGrpcChannelFactory(new PeerGrpcChannelFactoryOptions(), clock);

        var certStoreA = new SqliteTransferSessionCertificateStore(_dbA.ContextFactory, clock);
        var certStoreB = new SqliteTransferSessionCertificateStore(_dbB.ContextFactory, clock);

        _eventBusA = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);
        _eventBusB = new InMemoryEventBus(NullLogger<InMemoryEventBus>.Instance);

        _transportA = new TransferPeerTransport(_channelFactory, certStoreA, _eventBusA, clock);
        _transportB = new TransferPeerTransport(_channelFactory, certStoreB, _eventBusB, clock);

        var policyHolder = new RelayHostingPolicyHolder(RelayHostingPolicy.Default with
        {
            EnabledFeatures = RelayHostingPolicy.Default.EnabledFeatures | RelayFeatureFlags.SwarmTracker
        });
        _stunService = new UdpStunResponderService(
            policyHolder,
            new UdpStunResponderOptions
            {
                BindAddress = IPAddress.Loopback,
                Port = 0
            },
            clock);
        await _stunService.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _transportA.DisposeAsync();
        await _transportB.DisposeAsync();
        _eventBusA.Dispose();
        _eventBusB.Dispose();
        await _stunService.StopAsync(CancellationToken.None);
        await _stunService.DisposeAsync();
        _channelFactory.Dispose();
        _dbA.Dispose();
        _dbB.Dispose();
    }

    [Test]
    public async Task DirectWebRtcDtlsSctp_LoopbackWithLiveStun_Transfers512KbAndTearsDownCleanly()
    {
        // Pre-activation stealth: zero active sockets/connections before transfer start
        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);

        var sessionId = TransferSessionId.New();
        var peerAId = PublicIdentityId.New();
        var peerBId = PublicIdentityId.New();
        var authToken = StreamAuthToken.Generate();

        IPEndPoint stunEp = _stunService.BoundEndpoint!;
        var stunDescriptor = new StunEndpointDescriptor(
            EndpointHost.Parse(stunEp.Address.ToString()),
            PortNumber.FromInt(stunEp.Port));

        ITransferPeerConnection connA = await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, peerBId, IsInitiator: true, ExchangeStunEndpoint: stunDescriptor));
        ITransferPeerConnection connB = await _transportB.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, peerAId, IsInitiator: false, ExchangeStunEndpoint: stunDescriptor));

        _transportA.ActiveConnectionCount.Should().Be(1);
        _transportB.ActiveConnectionCount.Should().Be(1);

        // Wire trickle ICE candidate relay between peer A and peer B (simulating E2E signaling channel)
        using IDisposable subA = _eventBusA.Subscribe<TransferIceCandidateDiscoveredEvent>(async (evt, ct) =>
        {
            if (evt.SessionId == sessionId && evt.RemotePeerId == peerBId)
            {
                try
                {
                    await connB.AddRemoteIceCandidateAsync(evt.Candidate, evt.CandidateMid ?? string.Empty, ct);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or TransferTransportException)
                {
                }
            }
        });

        using IDisposable subB = _eventBusB.Subscribe<TransferIceCandidateDiscoveredEvent>(async (evt, ct) =>
        {
            if (evt.SessionId == sessionId && evt.RemotePeerId == peerAId)
            {
                try
                {
                    await connA.AddRemoteIceCandidateAsync(evt.Candidate, evt.CandidateMid ?? string.Empty, ct);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or TransferTransportException)
                {
                }
            }
        });

        // 1. Exchange SDP Offer & Answer with E2E-pinned DTLS SHA-256 fingerprints
        string offerSdp = await connA.CreateOfferAsync();
        connA.LocalDtlsFingerprint.Should().NotBeNull();

        string answerSdp = await connB.AcceptOfferAndCreateAnswerAsync(offerSdp, connA.LocalDtlsFingerprint);
        connB.LocalDtlsFingerprint.Should().NotBeNull();
        connB.RemoteDtlsFingerprint.Should().Be(connA.LocalDtlsFingerprint);

        await connA.ApplyRemoteAnswerAsync(answerSdp, connB.LocalDtlsFingerprint);
        connA.RemoteDtlsFingerprint.Should().Be(connB.LocalDtlsFingerprint);

        // 2. Wait for ICE + DTLS + SCTP + mutual HandshakeFrame authentication
        Task<ITransferDataChannel> openATask = connA.WaitForAuthenticatedChannelAsync(authToken, peerAId).AsTask();
        Task<ITransferDataChannel> openBTask = connB.WaitForAuthenticatedChannelAsync(authToken, peerBId).AsTask();
        await Task.WhenAll(openATask, openBTask);

        ITransferDataChannel channelA = openATask.Result;
        ITransferDataChannel channelB = openBTask.Result;

        channelA.IsOpen.Should().BeTrue();
        channelB.IsOpen.Should().BeTrue();
        channelA.TransportKind.Should().Be(TransferTransportKind.DirectWebRtcDtls);
        channelA.VerifiedRemoteCertSha256.Should().Be(connB.LocalDtlsFingerprint);
        channelB.VerifiedRemoteCertSha256.Should().Be(connA.LocalDtlsFingerprint);

        // 3. Stream 512 KB (8 x 64 KB chunks) over DTLS/SCTP and verify byte-for-byte integrity
        const int chunkCount = 8;
        const int chunkSize = 65_536;
        byte[][] sentChunks = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            sentChunks[i] = RandomNumberGenerator.GetBytes(chunkSize);
        }

        Task sendAll = Task.Run(async () =>
        {
            for (int i = 0; i < chunkCount; i++)
            {
                await channelA.SendFrameAsync(new ChunkDataFrame(
                    ChunkIndex.FromInt(i),
                    PlaintextChunkData.FromBytes(sentChunks[i])));
            }
        });

        byte[][] receivedChunks = new byte[chunkCount][];
        Task receiveAll = Task.Run(async () =>
        {
            for (int i = 0; i < chunkCount; i++)
            {
                var frame = (ChunkDataFrame)await channelB.ReceiveFrameAsync();
                receivedChunks[frame.ChunkIndex.Value] = frame.Data.Span.ToArray();
            }
        });

        await Task.WhenAll(sendAll, receiveAll).WaitAsync(TimeSpan.FromSeconds(15));

        for (int i = 0; i < chunkCount; i++)
        {
            receivedChunks[i].Should().Equal(sentChunks[i]);
        }

        // 4. CloseSessionConnectionsAsync tears down all sockets immediately
        await _transportA.CloseSessionConnectionsAsync(sessionId);
        await _transportB.CloseSessionConnectionsAsync(sessionId);

        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);
        channelA.IsOpen.Should().BeFalse();
        channelB.IsOpen.Should().BeFalse();
    }

    [Test]
    public async Task DirectWebRtc_DtlsFingerprintMismatch_RejectsBeforeNativeIngestion()
    {
        var sessionId = TransferSessionId.New();
        ITransferPeerConnection connA = await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: true));
        ITransferPeerConnection connB = await _transportB.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: false));

        string offerSdp = await connA.CreateOfferAsync();
        CertSha256Fingerprint wrongFingerprint = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var act = async () => await connB.AcceptOfferAndCreateAnswerAsync(offerSdp, wrongFingerprint);

        var ex = await act.Should().ThrowAsync<TransferTransportException>();
        ex.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);

        await _transportA.CloseSessionConnectionsAsync(sessionId);
        await _transportB.CloseSessionConnectionsAsync(sessionId);
        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);
    }

    [Test]
    public async Task DirectWebRtc_ProhibitedVideoMediaInSdp_RejectsWithProhibitedSdpMedia()
    {
        var sessionId = TransferSessionId.New();
        ITransferPeerConnection connA = await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: true));
        ITransferPeerConnection connB = await _transportB.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: false));

        string offerSdp = await connA.CreateOfferAsync();
        string poisonedOffer = offerSdp + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n";

        var act = async () => await connB.AcceptOfferAndCreateAnswerAsync(poisonedOffer, connA.LocalDtlsFingerprint);

        var ex = await act.Should().ThrowAsync<TransferTransportException>();
        ex.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.ProhibitedSdpMedia);

        await _transportA.CloseSessionConnectionsAsync(sessionId);
        await _transportB.CloseSessionConnectionsAsync(sessionId);
    }

    [Test]
    public async Task DirectWebRtc_InvalidHandshakeToken_AbortsAndDisposesChannel()
    {
        var sessionId = TransferSessionId.New();
        var peerAId = PublicIdentityId.New();
        var peerBId = PublicIdentityId.New();
        var tokenA = StreamAuthToken.Generate();
        var tokenB = StreamAuthToken.Generate();

        ITransferPeerConnection connA = await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, peerBId, IsInitiator: true, HandshakeTimeout: TimeSpan.FromSeconds(10)));
        ITransferPeerConnection connB = await _transportB.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, peerAId, IsInitiator: false, HandshakeTimeout: TimeSpan.FromSeconds(10)));

        using IDisposable subA = _eventBusA.Subscribe<TransferIceCandidateDiscoveredEvent>(async (evt, ct) =>
        {
            if (evt.SessionId == sessionId && evt.RemotePeerId == peerBId)
            {
                try
                {
                    await connB.AddRemoteIceCandidateAsync(evt.Candidate, evt.CandidateMid ?? string.Empty, ct);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or TransferTransportException)
                {
                }
            }
        });
        using IDisposable subB = _eventBusB.Subscribe<TransferIceCandidateDiscoveredEvent>(async (evt, ct) =>
        {
            if (evt.SessionId == sessionId && evt.RemotePeerId == peerAId)
            {
                try
                {
                    await connA.AddRemoteIceCandidateAsync(evt.Candidate, evt.CandidateMid ?? string.Empty, ct);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException or TransferTransportException)
                {
                }
            }
        });

        string offer = await connA.CreateOfferAsync();
        string answer = await connB.AcceptOfferAndCreateAnswerAsync(offer, connA.LocalDtlsFingerprint);
        await connA.ApplyRemoteAnswerAsync(answer, connB.LocalDtlsFingerprint);

        Task<ITransferDataChannel> authA = connA.WaitForAuthenticatedChannelAsync(tokenA, peerAId).AsTask();
        Task<ITransferDataChannel> authB = connB.WaitForAuthenticatedChannelAsync(tokenB, peerBId).AsTask();

        var exA = await FluentActions.Awaiting(() => authA).Should().ThrowAsync<TransferTransportException>();
        exA.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.InvalidHandshakeToken);

        var exB = await FluentActions.Awaiting(() => authB).Should().ThrowAsync<TransferTransportException>();
        exB.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.InvalidHandshakeToken);

        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);
    }

    [Test]
    public async Task DirectWebRtc_ExceedingPerSessionPeerCap_ThrowsSessionPeerCapExceeded()
    {
        var sessionId = TransferSessionId.New();

        await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: true, MaxPeersPerSession: 2));
        await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: true, MaxPeersPerSession: 2));

        var act = async () => await _transportA.CreateDirectConnectionAsync(
            new DirectPeerConnectionOptions(sessionId, PublicIdentityId.New(), IsInitiator: true, MaxPeersPerSession: 2));

        var ex = await act.Should().ThrowAsync<TransferTransportException>();
        ex.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.SessionPeerCapExceeded);

        await _transportA.CloseSessionConnectionsAsync(sessionId);
        _transportA.ActiveConnectionCount.Should().Be(0);
    }
}
