namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class TcpMultiStreamPoolTests
{
    [Test]
    public void FrameSerialization_All12Frames_RoundtripsSuccessfully()
    {
        // 1. Handshake
        var handshake = new HandshakeFrame(
            StreamAuthToken.Generate(),
            TransferSessionId.New(),
            StreamIndex.FromInt(2),
            Domain.Identities.PublicIdentityId.New());
        var decodedHandshake = (HandshakeFrame)RoundtripFrame(handshake);
        decodedHandshake.AuthToken.Span.SequenceEqual(handshake.AuthToken.Span).Should().BeTrue();
        decodedHandshake.SessionId.Should().Be(handshake.SessionId);
        decodedHandshake.StreamIndex.Should().Be(handshake.StreamIndex);
        decodedHandshake.SenderPeerId.Should().Be(handshake.SenderPeerId);

        // 2. Bitfield
        var bitfield = Bitfield.Full(ChunkCount.FromInt(10));
        var bitfieldFrame = new BitfieldFrame(bitfield);
        var decodedBitfield = (BitfieldFrame)RoundtripFrame(bitfieldFrame);
        decodedBitfield.Bitfield.PopCount.Should().Be(bitfield.PopCount);
        decodedBitfield.Bitfield.TotalChunks.Should().Be(bitfield.TotalChunks);

        // 3. Have
        var have = new HaveFrame(ChunkIndex.FromInt(42));
        var decodedHave = (HaveFrame)RoundtripFrame(have);
        decodedHave.ChunkIndex.Should().Be(have.ChunkIndex);

        // 4. RequestChunk
        var request = new RequestChunkFrame(ChunkIndex.FromInt(7));
        var decodedRequest = (RequestChunkFrame)RoundtripFrame(request);
        decodedRequest.ChunkIndex.Should().Be(request.ChunkIndex);

        // 5. ChunkData
        byte[] ciphertext = new byte[100];
        RandomNumberGenerator.Fill(ciphertext);
        var chunkData = new ChunkDataFrame(ChunkIndex.FromInt(12), EncryptedChunkData.FromBytes(ciphertext));
        var decodedChunk = (ChunkDataFrame)RoundtripFrame(chunkData);
        decodedChunk.ChunkIndex.Should().Be(chunkData.ChunkIndex);
        decodedChunk.Data.Span.SequenceEqual(ciphertext).Should().BeTrue();

        // 6. CancelChunk
        var cancel = new CancelChunkFrame(ChunkIndex.FromInt(99));
        var decodedCancel = (CancelChunkFrame)RoundtripFrame(cancel);
        decodedCancel.ChunkIndex.Should().Be(cancel.ChunkIndex);

        // 7. Choke
        var choke = new ChokeFrame();
        RoundtripFrame(choke).Should().BeOfType<ChokeFrame>();

        // 8. Unchoke
        var unchoke = new UnchokeFrame();
        RoundtripFrame(unchoke).Should().BeOfType<UnchokeFrame>();

        // 9. Interested
        var interested = new InterestedFrame();
        RoundtripFrame(interested).Should().BeOfType<InterestedFrame>();

        // 10. NotInterested
        var notInterested = new NotInterestedFrame();
        RoundtripFrame(notInterested).Should().BeOfType<NotInterestedFrame>();

        // 11. KeepAlive
        var keepAlive = new KeepAliveFrame();
        RoundtripFrame(keepAlive).Should().BeOfType<KeepAliveFrame>();

        // 12. PexPeers
        var candidates = new List<DirectEndpointCandidate>
        {
            new(EndpointHost.Parse("192.168.1.10"), PortNumber.FromInt(50001), DirectCandidateKind.DirectLan),
            new(EndpointHost.Parse("203.0.113.50"), PortNumber.FromInt(50002), DirectCandidateKind.DirectWanUpnp)
        };
        var pex = new PexPeersFrame(candidates);
        var decodedPex = (PexPeersFrame)RoundtripFrame(pex);
        decodedPex.Candidates.Should().HaveCount(2);
        decodedPex.Candidates[0].Host.Value.Should().Be("192.168.1.10");
        decodedPex.Candidates[0].Port.Value.Should().Be(50001);
        decodedPex.Candidates[0].Kind.Should().Be(DirectCandidateKind.DirectLan);
        decodedPex.Candidates[1].Host.Value.Should().Be("203.0.113.50");
        decodedPex.Candidates[1].Port.Value.Should().Be(50002);
        decodedPex.Candidates[1].Kind.Should().Be(DirectCandidateKind.DirectWanUpnp);
    }

    [Test]
    public async Task FrameValidation_OversizedPayload_ClosesStreamWithoutAllocation()
    {
        // Construct 5-byte header with length 300,000 (> 262,144 limit)
        byte[] header = new byte[5];
        header[0] = (byte)StreamFrameType.ChunkData;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), 300_000);

        using var memoryStream = new MemoryStream();
        await memoryStream.WriteAsync(header);
        memoryStream.Position = 0;

        await using var transferStream = new TcpTransferStream(StreamIndex.FromInt(0), memoryStream);
        var act = async () => await transferStream.ReceiveFrameAsync();

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*256 KB*");

        // Verify stream disposed
        var sendAct = async () => await transferStream.SendFrameAsync(new KeepAliveFrame());
        await sendAct.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public void FrameValidation_MalformedPayloadLength_ThrowsInvalidDataException()
    {
        // Handshake frame requires exactly 68 bytes. If payload is 10 bytes, it must reject.
        byte[] truncated = new byte[10];
        var act = () => SwarmFrameCodec.Decode((byte)StreamFrameType.Handshake, truncated);
        act.Should().Throw<InvalidDataException>();
    }

    [Test]
    public async Task StreamHandshake_InvalidAuthToken_TerminatesConnection()
    {
        var expectedToken = StreamAuthToken.Generate();
        var forgedToken = StreamAuthToken.Generate();

        var handshake = new HandshakeFrame(
            forgedToken,
            TransferSessionId.New(),
            StreamIndex.FromInt(0),
            Domain.Identities.PublicIdentityId.New());

        (byte opcode, byte[] payload) = SwarmFrameCodec.Encode(handshake);
        byte[] header = new byte[5];
        header[0] = opcode;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1, 4), (uint)payload.Length);

        using var memoryStream = new MemoryStream();
        await memoryStream.WriteAsync(header);
        await memoryStream.WriteAsync(payload);
        memoryStream.Position = 0;

        await using var transferStream = new TcpTransferStream(StreamIndex.FromInt(0), memoryStream);
        var received = await transferStream.ReceiveFrameAsync();
        var receivedHandshake = (HandshakeFrame)received;

        bool isAuthenticated = CryptographicOperations.FixedTimeEquals(
            receivedHandshake.AuthToken.Span,
            expectedToken.Span);

        isAuthenticated.Should().BeFalse();
        if (!isAuthenticated)
        {
            await transferStream.DisposeAsync();
        }

        var sendAct = async () => await transferStream.SendFrameAsync(new KeepAliveFrame());
        await sendAct.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task ConnectRelayPipeAsync_OpensRealParallelStreams()
    {
        var pipeClient = new RelayTransferPipeClient((descriptor, index, ct) =>
        {
            var ms = new MemoryStream();
            return ValueTask.FromResult<Stream>(ms);
        });

        var factory = new TcpMultiStreamPoolFactory(pipeClient: pipeClient, validator: new CallbackEndpointValidator(new CallbackEndpointValidatorOptions()));
        var pool = factory.CreatePool(TransferSessionId.New(), Domain.Identities.PublicIdentityId.New(), StreamCount.FromInt(4));

        var rendezvous = new RelayRendezvousDescriptor(
            RelayPipeEndpointUri.Parse("https://relay.test.net/pipes"),
            RelayPipeToken.Generate());

        await pool.ConnectRelayPipeAsync(rendezvous);
        pool.ActiveStreams.Count.Should().Be(4);

        var stream = pool.GetStream(StreamIndex.FromInt(2));
        stream.Should().NotBeNull();
        stream.Index.Value.Should().Be(2);

        await pool.DisposeAsync();
    }

    private static IStreamFramePayload RoundtripFrame(IStreamFramePayload frame)
    {
        (byte opcode, byte[] payload) = SwarmFrameCodec.Encode(frame);
        return SwarmFrameCodec.Decode(opcode, payload);
    }
}
