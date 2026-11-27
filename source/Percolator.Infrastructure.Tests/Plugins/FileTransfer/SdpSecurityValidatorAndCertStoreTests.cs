namespace Percolator.Infrastructure.Tests.Plugins.FileTransfer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;
using Percolator.Infrastructure.Tests.Persistence.TestSupport;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class SdpSecurityValidatorAndCertStoreTests
{
    private SqliteTestDb _db = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SqliteTestDb();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
    }

    [Test]
    public void SdpSecurityValidator_ValidApplicationSdpWithMatchingFingerprint_Succeeds()
    {
        CertSha256Fingerprint expected = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 1, 2, 3, 4 });
        string sdp = BuildValidSdp(expected);

        var result = SdpSecurityValidator.ValidateAndExtractFingerprint(sdp, expected);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
    }

    [TestCase("m=audio 9 UDP/TLS/RTP/SAVPF 111")]
    [TestCase("m=video 9 UDP/TLS/RTP/SAVPF 96")]
    [TestCase("m=text 9 UDP/TLS/RTP/SAVPF 98")]
    [TestCase("m=image 9 UDPTL t38")]
    public void SdpSecurityValidator_ProhibitedMediaSection_RejectsWithProhibitedSdpMedia(string prohibitedLine)
    {
        CertSha256Fingerprint expected = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 5, 6, 7 });
        string sdp = BuildValidSdp(expected) + prohibitedLine + "\r\n";

        var result = SdpSecurityValidator.ValidateAndExtractFingerprint(sdp, expected);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(TransferTransportErrorCodes.ProhibitedSdpMedia);
    }

    [Test]
    public void SdpSecurityValidator_FingerprintMismatch_RejectsWithDtlsFingerprintMismatch()
    {
        CertSha256Fingerprint actual = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 0xAA });
        CertSha256Fingerprint expected = CertSha256Fingerprint.ComputeFromCertificateDer(new byte[] { 0xBB });
        string sdp = BuildValidSdp(actual);

        var result = SdpSecurityValidator.ValidateAndExtractFingerprint(sdp, expected);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);
    }

    [Test]
    public void SdpSecurityValidator_MissingFingerprintOrOversized_Rejects()
    {
        string missingFp = "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n";
        SdpSecurityValidator.ValidateAndExtractFingerprint(missingFp)
            .Error.Code.Should().Be(TransferTransportErrorCodes.DtlsFingerprintMismatch);

        string oversized = new string('a', SdpSecurityValidator.MaxSdpLengthChars + 1);
        SdpSecurityValidator.ValidateAndExtractFingerprint(oversized)
            .Error.Code.Should().Be(TransferTransportErrorCodes.ProhibitedSdpMedia);
    }

    [Test]
    public async Task SqliteTransferSessionCertificateStore_PersistsAcrossDbContexts_AndZeroizesOnDispose()
    {
        var store = new SqliteTransferSessionCertificateStore(_db.ContextFactory, new SystemDateTimeProvider());
        var sessionId = TransferSessionId.New();

        CertSha256Fingerprint createdFingerprint;
        byte[] createdCertDer;

        using (TransferSessionCertificateMaterial created = await store.GetOrCreateAsync(sessionId))
        {
            createdFingerprint = created.Fingerprint;
            createdCertDer = created.CertificateDer.ToArray();
            created.PrivateKeyPkcs8.Length.Should().BeGreaterThan(32);

            // Second GetOrCreateAsync from fresh DbContext returns identical persisted fingerprint
            using TransferSessionCertificateMaterial reloaded = await store.GetOrCreateAsync(sessionId);
            reloaded.Fingerprint.Should().Be(createdFingerprint);
            reloaded.CertificateDer.ToArray().Should().Equal(createdCertDer);
        }

        // Verify GetAsync returns persisted material
        using (TransferSessionCertificateMaterial? fetched = await store.GetAsync(sessionId))
        {
            fetched.Should().NotBeNull();
            fetched!.Fingerprint.Should().Be(createdFingerprint);
        }

        // Verify TransferSessionCertificateMaterial.Dispose zeroes backing PKCS#8 buffer
        byte[] privateKeyBuf = RandomNumberGenerator.GetBytes(64);
        var material = new TransferSessionCertificateMaterial(createdFingerprint, createdCertDer, privateKeyBuf);
        material.Dispose();
        privateKeyBuf.All(b => b == 0).Should().BeTrue();

        // Delete removes persisted row
        await store.DeleteAsync(sessionId);
        (await store.GetAsync(sessionId)).Should().BeNull();
    }

    [Test]
    public async Task SwarmFrameCodec_EncodePooledAndReadFramePooledAsync_RoundtripAndEnforceLimit()
    {
        byte[] chunkBytes = RandomNumberGenerator.GetBytes(65_536);
        var chunkFrame = new ChunkDataFrame(ChunkIndex.FromInt(7), PlaintextChunkData.FromBytes(chunkBytes));

        using (PooledEncodedFrame encoded = SwarmFrameCodec.EncodePooled(chunkFrame))
        {
            var decoded = (ChunkDataFrame)SwarmFrameCodec.DecodeFramed(encoded.Span);
            decoded.ChunkIndex.Value.Should().Be(7);
            decoded.Data.Span.SequenceEqual(chunkBytes).Should().BeTrue();

            using var ms = new MemoryStream(encoded.Span.ToArray());
            var streamDecoded = (ChunkDataFrame)await SwarmFrameCodec.ReadFramePooledAsync(ms);
            streamDecoded.ChunkIndex.Value.Should().Be(7);
            streamDecoded.Data.Span.SequenceEqual(chunkBytes).Should().BeTrue();
        }

        // Oversized header rejection
        byte[] badHeader = new byte[5];
        badHeader[0] = (byte)StreamFrameType.ChunkData;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(badHeader.AsSpan(1, 4), SwarmFrameCodec.MaxPayloadSizeBytes + 1);

        var actDecode = () => SwarmFrameCodec.DecodeFramed(badHeader);
        actDecode.Should().Throw<InvalidDataException>();

        using var badStream = new MemoryStream(badHeader);
        var actStream = async () => await SwarmFrameCodec.ReadFramePooledAsync(badStream);
        await actStream.Should().ThrowAsync<InvalidDataException>();
    }

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
        byte[] payload = new byte[100];
        RandomNumberGenerator.Fill(payload);
        var chunkData = new ChunkDataFrame(ChunkIndex.FromInt(12), PlaintextChunkData.FromBytes(payload));
        var decodedChunk = (ChunkDataFrame)RoundtripFrame(chunkData);
        decodedChunk.ChunkIndex.Should().Be(chunkData.ChunkIndex);
        decodedChunk.Data.Span.SequenceEqual(payload).Should().BeTrue();

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
            new(EndpointHost.Parse("192.168.1.10"), PortNumber.FromInt(49200), DirectCandidateKind.DirectLan),
            new(EndpointHost.Parse("203.0.113.50"), PortNumber.FromInt(49201), DirectCandidateKind.DirectWanUpnp)
        };
        var pex = new PexPeersFrame(candidates);
        var decodedPex = (PexPeersFrame)RoundtripFrame(pex);
        decodedPex.Candidates.Should().HaveCount(2);
        decodedPex.Candidates[0].Host.Value.Should().Be("192.168.1.10");
        decodedPex.Candidates[0].Port.Value.Should().Be(49200);
        decodedPex.Candidates[0].Kind.Should().Be(DirectCandidateKind.DirectLan);
        decodedPex.Candidates[1].Host.Value.Should().Be("203.0.113.50");
        decodedPex.Candidates[1].Port.Value.Should().Be(49201);
        decodedPex.Candidates[1].Kind.Should().Be(DirectCandidateKind.DirectWanUpnp);
    }

    [Test]
    public void FrameValidation_MalformedPayloadLength_ThrowsInvalidDataException()
    {
        byte[] truncated = new byte[10];
        var act = () => SwarmFrameCodec.Decode((byte)StreamFrameType.Handshake, truncated);
        act.Should().Throw<InvalidDataException>();
    }

    private static IStreamFramePayload RoundtripFrame(IStreamFramePayload frame)
    {
        (byte opcode, byte[] payload) = SwarmFrameCodec.Encode(frame);
        return SwarmFrameCodec.Decode(opcode, payload);
    }

    private static string BuildValidSdp(CertSha256Fingerprint fingerprint) =>
        "v=0\r\n" +
        "o=- 123456 2 IN IP4 127.0.0.1\r\n" +
        "s=-\r\n" +
        "t=0 0\r\n" +
        "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n" +
        "c=IN IP4 0.0.0.0\r\n" +
        $"a=fingerprint:sha-256 {fingerprint.ToColonHexUpper()}\r\n" +
        "a=setup:actpass\r\n" +
        "a=mid:0\r\n" +
        "a=sctp-port:5000\r\n";
}
