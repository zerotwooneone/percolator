namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Apps.FileTransfer.Domain.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class FileTransferTrackerAndPipeIntegrationTests
{
    [Test]
    public async Task SwarmTracker_DefaultPolicyDisabled_RejectsAnnounceAndDoesNotBindStun()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);

        relay.StunEndpoint.Should().BeNull("embedded UDP STUN must not bind when SwarmTracker is OFF");

        using var scope = alice.Services.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IDistributedTrackerPort>();
        var act = async () => await tracker.AnnounceAsync(
            SwarmRendezvousId.Generate(),
            EphemeralSwarmPeerId.Generate(),
            SwarmPeerRole.Seeder,
            relay.Endpoint);

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    [Test]
    public async Task SwarmTracker_Enabled_BlindedAnnounceDiscoverUnregisterAndLiveStunProbe_WritesZeroBytesToDisk()
    {
        var trackerOnlyPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(trackerOnlyPolicy).ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        using var aliceScope = alice.Services.CreateScope();
        using var bobScope = bob.Services.CreateScope();
        var aliceTracker = aliceScope.ServiceProvider.GetRequiredService<IDistributedTrackerPort>();
        var bobTracker = bobScope.ServiceProvider.GetRequiredService<IDistributedTrackerPort>();

        var swarmId = SwarmRendezvousId.Generate();
        var aliceEphemeral = EphemeralSwarmPeerId.Generate();
        var bobEphemeral = EphemeralSwarmPeerId.Generate();

        // 1. Alice announces as Seeder over unauthenticated gRPC channel
        SwarmAnnounceResult aliceFirst = await aliceTracker.AnnounceAsync(
            swarmId,
            aliceEphemeral,
            SwarmPeerRole.Seeder,
            relay.Endpoint);

        aliceFirst.ActivePeers.Should().BeEmpty();
        aliceFirst.SupportsTransferPipe.Should().BeFalse("TransferPipe remains opt-in OFF by default");
        aliceFirst.StunEndpoint.Should().NotBeNull();

        // 2. Verify returned STUN endpoint answers a real RFC 5389 UDP Binding Request
        using (var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var localEp = (IPEndPoint)udpClient.Client.LocalEndPoint!;
            var stunTarget = new IPEndPoint(
                IPAddress.Parse(aliceFirst.StunEndpoint!.Host.Value),
                aliceFirst.StunEndpoint.Port.Value);

            byte[] txId = RandomNumberGenerator.GetBytes(12);
            byte[] req = new byte[Rfc5389StunCodec.HeaderSizeBytes];
            Rfc5389StunCodec.BuildBindingRequest(txId, req);

            await udpClient.SendAsync(req, stunTarget);
            using var stunCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            UdpReceiveResult reply = await udpClient.ReceiveAsync(stunCts.Token);

            Rfc5389StunCodec.TryParseBindingResponse(reply.Buffer, txId, out IPEndPoint? mapped).Should().BeTrue();
            mapped.Should().Be(localEp);
        }

        // 3. Bob announces as Leecher -> discovers Alice's blinded ephemeral peer ID
        SwarmAnnounceResult bobResult = await bobTracker.AnnounceAsync(
            swarmId,
            bobEphemeral,
            SwarmPeerRole.Leecher,
            relay.Endpoint);

        bobResult.ActivePeers.Should().ContainSingle(p => p == aliceEphemeral);
        relay.SwarmTrackerRegistry.GetActivePeerCount(swarmId.Span).Should().Be(2);

        // 4. Bob unregisters -> Alice sees empty swarm and RAM count drops to 1
        await bobTracker.UnregisterAsync(swarmId, bobEphemeral, relay.Endpoint);
        SwarmAnnounceResult aliceSecond = await aliceTracker.AnnounceAsync(
            swarmId,
            aliceEphemeral,
            SwarmPeerRole.Seeder,
            relay.Endpoint);

        aliceSecond.ActivePeers.Should().BeEmpty();

        // 5. Alice unregisters -> swarm purged from RAM, zero disk writes
        await aliceTracker.UnregisterAsync(swarmId, aliceEphemeral, relay.Endpoint);
        relay.SwarmTrackerRegistry.ActiveSwarmCount.Should().Be(0);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    [Test]
    public async Task TransferPipe_DisabledByDefault_RejectsWithPermissionDenied()
    {
        var trackerOnlyPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker);

        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(trackerOnlyPolicy).ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);

        var channel = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>()
            .GetOrCreateChannel(relay.Endpoint.ToUri(), ChannelPoolKind.Unauthenticated);
        var pipeClient = new TransferPipeService.TransferPipeServiceClient(channel);

        using var call = pipeClient.OpenPipe();
        await call.RequestStream.WriteAsync(new PipeChunkProto
        {
            RendezvousToken = ByteString.CopyFrom(PipeRendezvousToken.Generate().Span)
        });
        await call.RequestStream.CompleteAsync();

        var act = async () => await call.ResponseStream.MoveNext(CancellationToken.None);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task TransferPipe_Enabled_Streams512KbBidirectionallyWithBackpressureAndZeroDiskWrites()
    {
        var pipeEnabledPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.SwarmTracker | RelayFeatureFlags.TransferPipe,
            maxConcurrentPipes: 4,
            maxBytesPerSecondPerPipe: 50L * 1024 * 1024,
            maxBytesPerSession: 10L * 1024 * 1024,
            pipeIdleTimeout: TimeSpan.FromSeconds(10));

        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(pipeEnabledPolicy).ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        var aliceChannel = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>()
            .GetOrCreateChannel(relay.Endpoint.ToUri(), ChannelPoolKind.Unauthenticated);
        var bobChannel = bob.Services.GetRequiredService<IPeerGrpcChannelFactory>()
            .GetOrCreateChannel(relay.Endpoint.ToUri(), ChannelPoolKind.Unauthenticated);

        var alicePipe = new TransferPipeService.TransferPipeServiceClient(aliceChannel);
        var bobPipe = new TransferPipeService.TransferPipeServiceClient(bobChannel);

        var token = PipeRendezvousToken.Generate();
        var tokenBytes = ByteString.CopyFrom(token.Span);

        // 512 KB (> 256 KB per-direction RAM buffer) to exercise bounded channel backpressure
        byte[] alicePayload = RandomNumberGenerator.GetBytes(512 * 1024);
        byte[] bobPayload = RandomNumberGenerator.GetBytes(256 * 1024);

        using var aliceCall = alicePipe.OpenPipe();
        using var bobCall = bobPipe.OpenPipe();

        var aliceReceiveTask = ReadAllBytesAsync(aliceCall.ResponseStream);
        var bobReceiveTask = ReadAllBytesAsync(bobCall.ResponseStream);

        var aliceSendTask = SendChunksAsync(aliceCall.RequestStream, tokenBytes, alicePayload, chunkSize: 32 * 1024);
        var bobSendTask = SendChunksAsync(bobCall.RequestStream, tokenBytes, bobPayload, chunkSize: 32 * 1024);

        await Task.WhenAll(aliceSendTask, bobSendTask);
        byte[] receivedByAlice = await aliceReceiveTask;
        byte[] receivedByBob = await bobReceiveTask;

        receivedByBob.Should().Equal(alicePayload);
        receivedByAlice.Should().Equal(bobPayload);

        relay.TransferPipeBroker.ActivePipeCount.Should().Be(0);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    [Test]
    public async Task TransferPipe_EnforcesMaxConcurrentPipes_MaxBytesPerSession_And_PipeIdleTimeout()
    {
        var strictPolicy = new RelayHostingPolicy(
            enabledFeatures: RelayFeatureFlags.DefaultCore | RelayFeatureFlags.TransferPipe,
            maxConcurrentPipes: 1,
            maxBytesPerSecondPerPipe: 50L * 1024 * 1024,
            maxBytesPerSession: 64 * 1024, // 64 KB hard session cap
            pipeIdleTimeout: TimeSpan.FromSeconds(1)); // 1s idle timeout

        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(strictPolicy).ConfigureAwait(false);
        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);

        var channel = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>()
            .GetOrCreateChannel(relay.Endpoint.ToUri(), ChannelPoolKind.Unauthenticated);
        var pipeClient = new TransferPipeService.TransferPipeServiceClient(channel);

        // 1. MaxConcurrentPipes = 1: open pipe 1 and hold it open while pipe 2 tries to open
        var token1 = ByteString.CopyFrom(PipeRendezvousToken.Generate().Span);
        var token2 = ByteString.CopyFrom(PipeRendezvousToken.Generate().Span);

        using (var call1A = pipeClient.OpenPipe())
        using (var call1B = pipeClient.OpenPipe())
        {
            await call1A.RequestStream.WriteAsync(new PipeChunkProto { RendezvousToken = token1 });
            await call1B.RequestStream.WriteAsync(new PipeChunkProto { RendezvousToken = token1 });

            // Wait briefly until both joined pipe 1
            await Task.Delay(100);
            relay.TransferPipeBroker.ActivePipeCount.Should().Be(1);

            using (var call2 = pipeClient.OpenPipe())
            {
                await call2.RequestStream.WriteAsync(new PipeChunkProto { RendezvousToken = token2 });
                var actQuota = async () => await call2.ResponseStream.MoveNext(CancellationToken.None);
                var exQuota = await actQuota.Should().ThrowAsync<RpcException>();
                exQuota.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
            }

            // 2. MaxBytesPerSession = 64 KB: exceed cap on pipe 1 -> ResourceExhausted
            await call1A.RequestStream.WriteAsync(new PipeChunkProto
            {
                RendezvousToken = token1,
                Payload = ByteString.CopyFrom(new byte[48 * 1024])
            });
            await call1A.RequestStream.WriteAsync(new PipeChunkProto
            {
                RendezvousToken = token1,
                Payload = ByteString.CopyFrom(new byte[32 * 1024]) // Total 80 KB > 64 KB
            });

            var actSessionCap = async () =>
            {
                while (await call1A.ResponseStream.MoveNext(CancellationToken.None)) { }
            };
            var exCap = await actSessionCap.Should().ThrowAsync<RpcException>();
            exCap.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
        }

        // Wait briefly for cleanup of closed slot
        await Task.Delay(100);

        // 3. PipeIdleTimeout = 1s: unpaired caller times out with DeadlineExceeded
        using (var loneCall = pipeClient.OpenPipe())
        {
            await loneCall.RequestStream.WriteAsync(new PipeChunkProto
            {
                RendezvousToken = ByteString.CopyFrom(PipeRendezvousToken.Generate().Span)
            });

            var actTimeout = async () => await loneCall.ResponseStream.MoveNext(CancellationToken.None);
            var exTimeout = await actTimeout.Should().ThrowAsync<RpcException>();
            exTimeout.Which.StatusCode.Should().Be(StatusCode.DeadlineExceeded);
        }

        relay.TransferPipeBroker.ActivePipeCount.Should().Be(0);
        AssertZeroDiskFiles(relay.BlobStorageDirectory);
    }

    private static async Task SendChunksAsync(
        IClientStreamWriter<PipeChunkProto> writer,
        ByteString token,
        byte[] data,
        int chunkSize)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            int count = Math.Min(chunkSize, data.Length - offset);
            await writer.WriteAsync(new PipeChunkProto
            {
                RendezvousToken = token,
                Payload = ByteString.CopyFrom(data, offset, count)
            }).ConfigureAwait(false);
            offset += count;
        }

        await writer.CompleteAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IAsyncStreamReader<PipeChunkProto> reader)
    {
        using var ms = new MemoryStream();
        while (await reader.MoveNext(CancellationToken.None).ConfigureAwait(false))
        {
            if (!reader.Current.Payload.IsEmpty)
            {
                reader.Current.Payload.WriteTo(ms);
            }
        }

        return ms.ToArray();
    }

    private static void AssertZeroDiskFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }
}
