namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Serialization;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class SealedEnvelopeUnwrapperTests
{
    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    [Test]
    public void Encode_EncryptsHeaderMetadata_DoesNotExposeSenderOrChannelIdInCleartext()
    {
        var channelId = ChannelId.New();
        var senderId = PublicIdentityId.New();
        var deviceId = new DeviceId(77);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        var frame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            channelId,
            senderId,
            deviceId,
            payload);

        frame[0].Should().Be(SealedWireFraming.SealedMagicByte);
        frame.Length.Should().Be(SealedWireFraming.HeaderSize + payload.Length);

        // Ensure cleartext ChannelId and SenderId byte sequences do NOT appear anywhere in the sealed frame
        var channelBytes = channelId.ToByteArray();
        var senderBytes = senderId.ToByteArray();

        ContainsSubsequence(frame, channelBytes).Should().BeFalse("ChannelId must be encrypted under AEAD and not appear in cleartext");
        ContainsSubsequence(frame, senderBytes).Should().BeFalse("SenderId must be encrypted under AEAD and not appear in cleartext");

        // Verify successful decode
        var decodeResult = SealedWireFraming.Decode(frame);
        decodeResult.IsSuccess.Should().BeTrue();
        decodeResult.Value.Kind.Should().Be(WireFrameKind.DirectRatchet);
        decodeResult.Value.ChannelId.Should().Be(channelId);
        decodeResult.Value.SenderId.Should().Be(senderId);
        decodeResult.Value.SenderDeviceId.Should().Be(deviceId);
        decodeResult.Value.PackedWireBytes.ToArray().Should().Equal(payload);
    }

    [Test]
    public void Unwrap_CorruptedWirePayload_ReturnsDomainFailureWithoutThrowing()
    {
        var packer = new ProtobufSessionWirePacker();
        var timeProvider = new FixedTimeProvider();
        var recipientId = PublicIdentityId.New();
        var unwrapper = new SealedEnvelopeUnwrapper(packer, recipientId, timeProvider);

        // Case 1: Frame too short (< 82 bytes)
        var shortBytes = new byte[20];
        RandomNumberGenerator.Fill(shortBytes);
        var envelopeShort = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            shortBytes,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res1 = unwrapper.Unwrap(envelopeShort);
        res1.IsFailure.Should().BeTrue();
        res1.Error!.Code.Should().Be("FRAME_TOO_SHORT");

        // Case 2: Unknown frame kind (wrong magic byte)
        var unknownKindFrame = new byte[90];
        unknownKindFrame[0] = 0xAA; // invalid magic byte
        var envelopeUnknownKind = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            unknownKindFrame,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res2 = unwrapper.Unwrap(envelopeUnknownKind);
        res2.IsFailure.Should().BeTrue();
        res2.Error!.Code.Should().Be("UNKNOWN_FRAME_KIND");

        // Case 3: Truncated / tampered frame (tag mismatch or truncated payload)
        var validFrame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            ChannelId.New(),
            PublicIdentityId.New(),
            new DeviceId(1),
            new byte[10]);

        // Tamper with ciphertext byte to trigger AEAD auth failure
        validFrame[validFrame.Length - 1] ^= 0xFF;

        var envelopeTruncated = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            validFrame,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res3 = unwrapper.Unwrap(envelopeTruncated);
        res3.IsFailure.Should().BeTrue();
        res3.Error!.Code.Should().Be("FRAME_TRUNCATED");

        // Case 4: Corrupted inner Protobuf bytes (inner payload is invalid protobuf)
        var corruptInner = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            ChannelId.New(),
            PublicIdentityId.New(),
            new DeviceId(1),
            new byte[] { 0xFF, 0xFE, 0xFD }); // invalid protobuf

        var envelopeCorruptedInner = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            corruptInner,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res4 = unwrapper.Unwrap(envelopeCorruptedInner);
        res4.IsFailure.Should().BeTrue();
        // Returns failure gracefully without throwing
    }

    [Test]
    public void Unwrap_ValidDirectRatchetFrame_RoundtripsSuccessfully()
    {
        var packer = new ProtobufSessionWirePacker();
        var timeProvider = new FixedTimeProvider();
        var recipientId = PublicIdentityId.New();
        var senderId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var deviceId = new DeviceId(42);

        var dhKey = DhPublicKey.FromSpan(new byte[32]);
        var header = new RatchetHeader(dhKey, 5, 2);
        var ciphertext = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var packedPayload = packer.PackDirectRatchetMessage(header, ciphertext);
        var frame = SealedWireFraming.Encode(WireFrameKind.DirectRatchet, channelId, senderId, deviceId, packedPayload);

        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            frame,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var unwrapper = new SealedEnvelopeUnwrapper(packer, recipientId, timeProvider);
        var result = unwrapper.Unwrap(envelope);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeOfType<InboundDirectEnvelope>();

        var direct = (InboundDirectEnvelope)result.Value!;
        direct.ChannelId.Should().Be(channelId);
        direct.RecipientIdentityId.Should().Be(recipientId);
        direct.SenderIdentityId.Should().Be(senderId);
        direct.SenderDeviceId.Should().Be(deviceId);
        direct.Header.Counter.Should().Be(5);
        direct.Ciphertext.ToArray().Should().Equal(ciphertext);
    }

    private static bool ContainsSubsequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }
}
