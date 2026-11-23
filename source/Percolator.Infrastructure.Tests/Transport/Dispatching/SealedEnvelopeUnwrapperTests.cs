namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using NUnit.Framework;
using Percolator.Application.Ingress;
using Percolator.Application.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Serialization;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class SealedEnvelopeUnwrapperTests
{
    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class MockLocalIdentityKeyStore : ILocalIdentityKeyStore
    {
        private readonly ConcurrentDictionary<PublicIdentityId, (EphemeralPrivateKey Priv, IdentityKey Pub)> _keys = new();

        public void SetKey(PublicIdentityId id, EphemeralPrivateKey priv, IdentityKey pub)
        {
            _keys[id] = (priv, pub);
        }

        public Task<EphemeralPrivateKey?> GetIdentityPrivateKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
        {
            if (_keys.TryGetValue(identityId, out var pair))
            {
                return Task.FromResult<EphemeralPrivateKey?>(EphemeralPrivateKey.FromSpan(pair.Priv.Span));
            }
            return Task.FromResult<EphemeralPrivateKey?>(null);
        }

        public Task<IdentityKey?> GetIdentityPublicKeyAsync(PublicIdentityId identityId, CancellationToken ct = default)
        {
            if (_keys.TryGetValue(identityId, out var pair))
            {
                return Task.FromResult<IdentityKey?>(pair.Pub);
            }
            return Task.FromResult<IdentityKey?>(null);
        }

        public Task<ProfileKey?> GetProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.FromResult<ProfileKey?>(null);

        public Task SaveIdentityKeysAsync(PublicIdentityId identityId, EphemeralPrivateKey privateKey, IdentityKey publicKey, ProfileKey profileKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ProfileKey> RotateProfileKeyAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task DeleteIdentityKeysAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class KeyPair : IDisposable
    {
        public EphemeralPrivateKey Priv { get; }
        public IdentityKey Pub { get; }

        public KeyPair(EphemeralPrivateKey priv, IdentityKey pub)
        {
            Priv = priv;
            Pub = pub;
        }

        public void Dispose() => Priv.Dispose();
    }

    private static KeyPair GenerateKeyPair()
    {
        Span<byte> privBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(privBytes);
        var priv = EphemeralPrivateKey.FromSpan(privBytes);
        Span<byte> pubBytes = stackalloc byte[32];
        SignalCryptoEngine.Curve25519Core.X25519BasePoint(pubBytes, priv.Span);
        var pub = IdentityKey.FromSpan(pubBytes);
        return new KeyPair(priv, pub);
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

    [Test]
    public void Encode_EncryptsHeaderMetadata_DoesNotExposeSenderOrChannelIdInCleartext()
    {
        var channelId = ChannelId.New();
        var senderId = PublicIdentityId.New();
        var recipientId = PublicIdentityId.New();
        var deviceId = new DeviceId(77);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        using var senderKeys = GenerateKeyPair();
        using var recipientKeys = GenerateKeyPair();

        var frame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            channelId,
            senderId,
            deviceId,
            senderKeys.Priv.Span,
            senderKeys.Pub,
            recipientKeys.Pub,
            payload);

        frame[0].Should().Be(SealedWireFraming.SealedMagicByte);
        frame[1].Should().Be(SealedWireFraming.Version1);

        // Ensure cleartext ChannelId and SenderId byte sequences do NOT appear anywhere in the sealed frame
        var channelBytes = channelId.ToByteArray();
        var senderBytes = senderId.ToByteArray();

        ContainsSubsequence(frame, channelBytes).Should().BeFalse("ChannelId must be encrypted under AEAD");
        ContainsSubsequence(frame, senderBytes).Should().BeFalse("SenderId must be encrypted under AEAD");
        ContainsSubsequence(frame, senderKeys.Pub.Span).Should().BeFalse("Sender public key must be encrypted under k1");

        // Verify successful decode using recipient private key
        var decodeResult = SealedWireFraming.Decode(frame, recipientKeys.Priv.Span, recipientKeys.Pub);
        decodeResult.IsSuccess.Should().BeTrue();
        decodeResult.Value.Kind.Should().Be(WireFrameKind.DirectRatchet);
        decodeResult.Value.ChannelId.Should().Be(channelId);
        decodeResult.Value.SenderId.Should().Be(senderId);
        decodeResult.Value.SenderDeviceId.Should().Be(deviceId);
        decodeResult.Value.SenderIdentityKey.Span.ToArray().Should().Equal(senderKeys.Pub.Span.ToArray());
        decodeResult.Value.PackedWireBytes.ToArray().Should().Equal(payload);
    }

    [Test]
    public void Decode_ThirdPartyWithoutRecipientPrivateKey_FailsToDecrypt()
    {
        var channelId = ChannelId.New();
        var senderId = PublicIdentityId.New();
        var deviceId = new DeviceId(77);
        var payload = new byte[] { 10, 20, 30, 40, 50 };

        using var senderKeys = GenerateKeyPair();
        using var recipientKeys = GenerateKeyPair();
        using var eavesdropperKeys = GenerateKeyPair();

        var frame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            channelId,
            senderId,
            deviceId,
            senderKeys.Priv.Span,
            senderKeys.Pub,
            recipientKeys.Pub,
            payload);

        // Third party attempts decode with their own private key
        var decodeResult = SealedWireFraming.Decode(frame, eavesdropperKeys.Priv.Span, eavesdropperKeys.Pub);
        decodeResult.IsFailure.Should().BeTrue();
        decodeResult.Error.Code.Should().Be("FRAME_AUTHENTICATION_FAILED");
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

        using var senderKeys = GenerateKeyPair();
        using var recipientKeys = GenerateKeyPair();

        var keyStore = new MockLocalIdentityKeyStore();
        keyStore.SetKey(recipientId, recipientKeys.Priv, recipientKeys.Pub);

        var dhKey = DhPublicKey.FromSpan(new byte[32]);
        var header = new RatchetHeader(dhKey, 5, 2);
        var ciphertext = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var packedPayload = packer.PackDirectRatchetMessage(header, ciphertext);
        var frame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            channelId,
            senderId,
            deviceId,
            senderKeys.Priv.Span,
            senderKeys.Pub,
            recipientKeys.Pub,
            packedPayload);

        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            frame,
            timeProvider.UtcNow,
            timeProvider.UtcNow.AddHours(1));

        var unwrapper = new SealedEnvelopeUnwrapper(packer, timeProvider, keyStore);
        var result = unwrapper.Unwrap(envelope, recipientId);

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

    [Test]
    public void Unwrap_MailboxEnvelope_PreservesEnqueuedAtUtcAsReceivedAtUtc()
    {
        var packer = new ProtobufSessionWirePacker();
        var timeProvider = new FixedTimeProvider();
        var recipientId = PublicIdentityId.New();
        var senderId = PublicIdentityId.New();
        var channelId = ChannelId.New();
        var deviceId = new DeviceId(42);

        using var senderKeys = GenerateKeyPair();
        using var recipientKeys = GenerateKeyPair();

        var keyStore = new MockLocalIdentityKeyStore();
        keyStore.SetKey(recipientId, recipientKeys.Priv, recipientKeys.Pub);

        var dhKey = DhPublicKey.FromSpan(new byte[32]);
        var header = new RatchetHeader(dhKey, 1, 0);
        var ciphertext = new byte[] { 0xAA, 0xBB };

        var packedPayload = packer.PackDirectRatchetMessage(header, ciphertext);
        var frame = SealedWireFraming.Encode(
            WireFrameKind.DirectRatchet,
            channelId,
            senderId,
            deviceId,
            senderKeys.Priv.Span,
            senderKeys.Pub,
            recipientKeys.Pub,
            packedPayload);

        var historicalEnqueuedAt = timeProvider.UtcNow.AddDays(-2);
        var envelope = new MailboxEnvelope(
            EnvelopeId.New(),
            BlindedRoutingToken.New(),
            frame,
            historicalEnqueuedAt,
            timeProvider.UtcNow.AddDays(5));

        var unwrapper = new SealedEnvelopeUnwrapper(packer, timeProvider, keyStore);
        var result = unwrapper.Unwrap(envelope, recipientId);

        result.IsSuccess.Should().BeTrue();
        result.Value!.ReceivedAtUtc.Should().Be(historicalEnqueuedAt,
            "Unwrapped mailbox envelope must preserve the relay enqueued timestamp");
    }

    [Test]
    public void Unwrap_CorruptedFrame_ReturnsFailureGracefully()
    {
        var packer = new ProtobufSessionWirePacker();
        var timeProvider = new FixedTimeProvider();
        var recipientId = PublicIdentityId.New();
        using var recipientKeys = GenerateKeyPair();

        var keyStore = new MockLocalIdentityKeyStore();
        keyStore.SetKey(recipientId, recipientKeys.Priv, recipientKeys.Pub);

        var unwrapper = new SealedEnvelopeUnwrapper(packer, timeProvider, keyStore);

        // 1. Truncated frame
        var truncated = new byte[20];
        var env1 = new MailboxEnvelope(EnvelopeId.New(), BlindedRoutingToken.New(), truncated, timeProvider.UtcNow, timeProvider.UtcNow.AddHours(1));
        var res1 = unwrapper.Unwrap(env1, recipientId);
        res1.IsFailure.Should().BeTrue();

        // 2. Corrupted magic byte
        var corruptMagic = new byte[SealedWireFraming.HeaderSize + 10];
        corruptMagic[0] = 0xFF;
        var env2 = new MailboxEnvelope(EnvelopeId.New(), BlindedRoutingToken.New(), corruptMagic, timeProvider.UtcNow, timeProvider.UtcNow.AddHours(1));
        var res2 = unwrapper.Unwrap(env2, recipientId);
        res2.IsFailure.Should().BeTrue();
    }

    [Test]
    public void Constructor_WithNullWirePacker_ThrowsArgumentNullException()
    {
        var act = () => new SealedEnvelopeUnwrapper(null!, new FixedTimeProvider(), new MockLocalIdentityKeyStore());
        act.Should().Throw<ArgumentNullException>().WithParameterName("wirePacker");
    }

    [Test]
    public void Constructor_WithNullTimeProvider_ThrowsArgumentNullException()
    {
        var act = () => new SealedEnvelopeUnwrapper(new ProtobufSessionWirePacker(), null!, new MockLocalIdentityKeyStore());
        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Test]
    public void Constructor_WithNullIdentityKeyStore_ThrowsArgumentNullException()
    {
        var act = () => new SealedEnvelopeUnwrapper(new ProtobufSessionWirePacker(), new FixedTimeProvider(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("identityKeyStore");
    }
}
