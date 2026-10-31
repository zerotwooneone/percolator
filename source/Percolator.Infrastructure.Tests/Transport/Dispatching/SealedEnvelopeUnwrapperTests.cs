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
    public void Unwrap_CorruptedWirePayload_ReturnsDomainFailureWithoutThrowing()
    {
        var packer = new ProtobufSessionWirePacker();
        var timeProvider = new FixedTimeProvider();
        var recipientId = PublicIdentityId.New();
        var unwrapper = new SealedEnvelopeUnwrapper(packer, timeProvider, recipientId);

        // Case 1: Frame too short (< 53 bytes)
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

        // Case 2: Unknown frame kind
        var unknownKindFrame = new byte[60];
        unknownKindFrame[0] = 0xAA; // invalid kind
        var envelopeUnknownKind = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            unknownKindFrame,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res2 = unwrapper.Unwrap(envelopeUnknownKind);
        res2.IsFailure.Should().BeTrue();
        res2.Error!.Code.Should().Be("UNKNOWN_FRAME_KIND");

        // Case 3: Truncated frame (payload length header says 100, but only 5 bytes follow)
        var headerOnly = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            ChannelId.New(),
            PublicIdentityId.New(),
            new DeviceId(1),
            new byte[5]);
        // Corrupt length field at offset 49 to say 1000
        headerOnly[49] = 0xE8;
        headerOnly[50] = 0x03;

        var envelopeTruncated = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            headerOnly,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var res3 = unwrapper.Unwrap(envelopeTruncated);
        res3.IsFailure.Should().BeTrue();
        res3.Error!.Code.Should().Be("FRAME_TRUNCATED");

        // Case 4: Corrupted inner Protobuf bytes
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

        var unwrapper = new SealedEnvelopeUnwrapper(packer, timeProvider, recipientId);
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
}
