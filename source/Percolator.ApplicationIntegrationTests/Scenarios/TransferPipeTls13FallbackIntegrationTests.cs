namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class TransferPipeTls13FallbackIntegrationTests
{
    private TestRelayServer _exchange = null!;
    private PeerGrpcChannelFactory _channelFactory = null!;
    private VaultCertHarness _peerAVault = null!;
    private VaultCertHarness _peerBVault = null!;
    private TransferPeerTransport _transportA = null!;
    private TransferPeerTransport _transportB = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        var policy = RelayHostingPolicy.Default with
        {
            EnabledFeatures = RelayHostingPolicy.Default.EnabledFeatures | RelayFeatureFlags.SwarmTracker | RelayFeatureFlags.TransferPipe,
            MaxConcurrentPipes = 4,
            MaxBytesPerSession = 10 * 1024 * 1024,
            MaxBytesPerSecondPerPipe = 50 * 1024 * 1024,
            PipeIdleTimeout = TimeSpan.FromSeconds(15)
        };

        _exchange = await TestRelayServer.StartAsync(policy);
        _channelFactory = new PeerGrpcChannelFactory(
            new PeerGrpcChannelFactoryOptions(),
            new SystemDateTimeProvider());

        _peerAVault = await VaultCertHarness.CreateAsync();
        _peerBVault = await VaultCertHarness.CreateAsync();

        _transportA = new TransferPeerTransport(_channelFactory, _peerAVault.CertStore);
        _transportB = new TransferPeerTransport(_channelFactory, _peerBVault.CertStore);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        await _transportA.DisposeAsync();
        await _transportB.DisposeAsync();
        _channelFactory.Dispose();
        await _peerAVault.DisposeAsync();
        await _peerBVault.DisposeAsync();
        await _exchange.DisposeAsync();
    }

    [Test]
    public async Task TransferPipeTls13Mtls_Bidirectional512KbStream_VerifiesPinnedCertsAndWritesZeroBytesToExchangeDisk()
    {
        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);

        var sessionId = TransferSessionId.New();
        var peerAId = PublicIdentityId.New();
        var peerBId = PublicIdentityId.New();
        var rendezvousToken = PipeRendezvousToken.Generate();
        var authToken = StreamAuthToken.Generate();

        CertSha256Fingerprint peerACertFp;
        using (var matA = await _peerAVault.CertStore.GetOrCreateAsync(sessionId))
        {
            peerACertFp = matA.Fingerprint;
        }

        CertSha256Fingerprint peerBCertFp;
        using (var matB = await _peerBVault.CertStore.GetOrCreateAsync(sessionId))
        {
            peerBCertFp = matB.Fingerprint;
        }

        var optionsA = new RelayedPipeConnectionOptions(
            sessionId,
            peerAId,
            peerBId,
            _exchange.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: true,
            ExpectedRemoteCertSha256: peerBCertFp,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(15));

        var optionsB = new RelayedPipeConnectionOptions(
            sessionId,
            peerBId,
            peerAId,
            _exchange.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: false,
            ExpectedRemoteCertSha256: peerACertFp,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(15));

        Task<ITransferDataChannel> openATask = _transportA.OpenRelayedPipeChannelAsync(optionsA).AsTask();
        Task<ITransferDataChannel> openBTask = _transportB.OpenRelayedPipeChannelAsync(optionsB).AsTask();
        await Task.WhenAll(openATask, openBTask);

        ITransferDataChannel channelA = openATask.Result;
        ITransferDataChannel channelB = openBTask.Result;

        channelA.IsOpen.Should().BeTrue();
        channelB.IsOpen.Should().BeTrue();
        channelA.TransportKind.Should().Be(TransferTransportKind.RelayedTransferPipeTls13);
        channelB.TransportKind.Should().Be(TransferTransportKind.RelayedTransferPipeTls13);
        channelA.VerifiedRemoteCertSha256.Should().Be(peerBCertFp);
        channelB.VerifiedRemoteCertSha256.Should().Be(peerACertFp);
        _transportA.ActiveConnectionCount.Should().Be(1);
        _transportB.ActiveConnectionCount.Should().Be(1);

        // Stream 512 KB (8 x 64 KB chunks) from A -> B and verify bidirectional control frame B -> A
        const int chunkCount = 8;
        const int chunkSize = 65_536;
        byte[][] sentChunks = new byte[chunkCount][];
        for (int i = 0; i < chunkCount; i++)
        {
            sentChunks[i] = RandomNumberGenerator.GetBytes(chunkSize);
        }

        Task sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < chunkCount; i++)
            {
                await channelA.SendFrameAsync(new ChunkDataFrame(
                    ChunkIndex.FromInt(i),
                    PlaintextChunkData.FromBytes(sentChunks[i])));
            }
        });

        byte[][] receivedChunks = new byte[chunkCount][];
        Task recvTask = Task.Run(async () =>
        {
            for (int i = 0; i < chunkCount; i++)
            {
                var frame = (ChunkDataFrame)await channelB.ReceiveFrameAsync();
                receivedChunks[frame.ChunkIndex.Value] = frame.Data.Span.ToArray();
            }

            await channelB.SendFrameAsync(new HaveFrame(ChunkIndex.FromInt(chunkCount - 1)));
        });

        await Task.WhenAll(sendTask, recvTask).WaitAsync(TimeSpan.FromSeconds(15));
        var ackFrame = (HaveFrame)await channelA.ReceiveFrameAsync();
        ackFrame.ChunkIndex.Value.Should().Be(chunkCount - 1);

        for (int i = 0; i < chunkCount; i++)
        {
            receivedChunks[i].Should().Equal(sentChunks[i]);
        }

        // Assert zero bytes written to Exchange disk
        if (Directory.Exists(_exchange.BlobStorageDirectory))
        {
            Directory.GetFiles(_exchange.BlobStorageDirectory, "*", SearchOption.AllDirectories)
                .Should().BeEmpty("TransferPipe must never write transfer payloads to Exchange disk");
        }

        await _transportA.CloseSessionConnectionsAsync(sessionId);
        await _transportB.CloseSessionConnectionsAsync(sessionId);

        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);

        // Wait briefly for Exchange gRPC stream teardown to release pipe broker slot
        for (int attempt = 0; attempt < 20 && _exchange.TransferPipeBroker.ActivePipeCount > 0; attempt++)
        {
            await Task.Delay(50);
        }

        _exchange.TransferPipeBroker.ActivePipeCount.Should().Be(0);
    }

    [Test]
    public async Task TransferPipeTls13Mtls_PinnedCertificateMismatch_RejectsHandshakeAndCleansUp()
    {
        var sessionId = TransferSessionId.New();
        var peerAId = PublicIdentityId.New();
        var peerBId = PublicIdentityId.New();
        var rendezvousToken = PipeRendezvousToken.Generate();
        var authToken = StreamAuthToken.Generate();

        CertSha256Fingerprint peerACertFp;
        using (var matA = await _peerAVault.CertStore.GetOrCreateAsync(sessionId))
        {
            peerACertFp = matA.Fingerprint;
        }

        CertSha256Fingerprint wrongExpectedFp = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 0x99, 0x88, 0x77 });

        var optionsA = new RelayedPipeConnectionOptions(
            sessionId,
            peerAId,
            peerBId,
            _exchange.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: true,
            ExpectedRemoteCertSha256: wrongExpectedFp,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(10));

        var optionsB = new RelayedPipeConnectionOptions(
            sessionId,
            peerBId,
            peerAId,
            _exchange.Endpoint,
            rendezvousToken,
            authToken,
            IsTlsServer: false,
            ExpectedRemoteCertSha256: peerACertFp,
            ExchangeSupportsTransferPipe: true,
            HandshakeTimeout: TimeSpan.FromSeconds(10));

        Task<ITransferDataChannel> openATask = _transportA.OpenRelayedPipeChannelAsync(optionsA).AsTask();
        Task<ITransferDataChannel> openBTask = _transportB.OpenRelayedPipeChannelAsync(optionsB).AsTask();

        var exA = await FluentActions.Awaiting(() => openATask).Should().ThrowAsync<TransferTransportException>();
        exA.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);

        await FluentActions.Awaiting(() => openBTask).Should().ThrowAsync<TransferTransportException>();

        _transportA.ActiveConnectionCount.Should().Be(0);
        _transportB.ActiveConnectionCount.Should().Be(0);
    }

    [Test]
    public async Task TransferPipeTls13Mtls_WhenExchangeDoesNotSupportTransferPipe_FailsWithDirectNatTraversalFailed()
    {
        var options = new RelayedPipeConnectionOptions(
            TransferSessionId.New(),
            PublicIdentityId.New(),
            PublicIdentityId.New(),
            _exchange.Endpoint,
            PipeRendezvousToken.Generate(),
            StreamAuthToken.Generate(),
            IsTlsServer: false,
            ExpectedRemoteCertSha256: CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 1, 2, 3 }),
            ExchangeSupportsTransferPipe: false);

        var act = async () => await _transportA.OpenRelayedPipeChannelAsync(options);

        var ex = await act.Should().ThrowAsync<TransferTransportException>();
        ex.Which.ErrorCode.Should().Be(TransferTransportErrorCodes.DirectNatTraversalFailed);
        _transportA.ActiveConnectionCount.Should().Be(0);
    }

    private sealed class VaultCertHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public SqliteTransferSessionCertificateStore CertStore { get; }

        private VaultCertHarness(SqliteConnection connection, SqliteTransferSessionCertificateStore certStore)
        {
            _connection = connection;
            CertStore = certStore;
        }

        public static async Task<VaultCertHarness> CreateAsync()
        {
            SQLitePCL.Batteries_V2.Init();
            var conn = new SqliteConnection("Data Source=:memory:");
            await conn.OpenAsync();
            var options = new DbContextOptionsBuilder<PercolatorDbContext>().UseSqlite(conn).Options;
            await using (var db = new PercolatorDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            var factory = new SingleConnectionContextFactory(conn);
            var store = new SqliteTransferSessionCertificateStore(factory, new SystemDateTimeProvider());
            return new VaultCertHarness(conn, store);
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        private sealed class SingleConnectionContextFactory : IDbContextFactory<PercolatorDbContext>
        {
            private readonly SqliteConnection _connection;
            public SingleConnectionContextFactory(SqliteConnection connection) => _connection = connection;

            public PercolatorDbContext CreateDbContext()
            {
                var options = new DbContextOptionsBuilder<PercolatorDbContext>().UseSqlite(_connection).Options;
                return new PercolatorDbContext(options);
            }
        }
    }
}
