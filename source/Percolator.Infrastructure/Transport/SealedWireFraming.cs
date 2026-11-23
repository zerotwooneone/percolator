namespace Percolator.Infrastructure.Transport;

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Security;

public enum WireFrameKind : byte
{
    DirectRatchet = 0x01,
    GroupBroadcast = 0x02,
    X3dhHandshake = 0x03
}

public static class SealedWireFraming
{
    public const byte SealedMagicByte = 0x53; // 'S'
    public const byte Version1 = 0x01;
    public const byte VersionGroupBroadcast = 0x02;
    public const int EphemeralPubKeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int EncryptedSenderKeySize = 32;

    public const int HeaderSize = 1 + 1 + EphemeralPubKeySize + NonceSize + TagSize + EncryptedSenderKeySize + NonceSize + TagSize; // 122 bytes
    public const int InnerHeaderSize = 1 + 16 + 16 + 4 + 12 + 4; // 53 bytes
    public const int GroupHeaderSize = 1 + 1 + 16 + 16 + 4 + 32 + 4; // 74 bytes

    private static readonly byte[] UnidentifiedDeliveryInfoPrefix = "UnidentifiedDelivery"u8.ToArray();

    public static byte[] EncodeGroupBroadcast(
        ChannelId channelId,
        Domain.Identities.PublicIdentityId senderId,
        DeviceId senderDeviceId,
        IdentityKey senderPublicKey,
        ReadOnlyMemory<byte> packedWireBytes)
    {
        ArgumentNullException.ThrowIfNull(senderPublicKey);
        var output = new byte[GroupHeaderSize + packedWireBytes.Length];
        output[0] = SealedMagicByte;
        output[1] = VersionGroupBroadcast;
        channelId.TryWriteBytes(output.AsSpan(2, 16));
        senderId.TryWriteBytes(output.AsSpan(18, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(34, 4), senderDeviceId.Value);
        senderPublicKey.Span.CopyTo(output.AsSpan(38, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(70, 4), (uint)packedWireBytes.Length);
        packedWireBytes.Span.CopyTo(output.AsSpan(GroupHeaderSize));
        return output;
    }

    public static byte[] Encode(
        WireFrameKind kind,
        ChannelId channelId,
        Domain.Identities.PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlySpan<byte> senderPrivateKey,
        IdentityKey senderPublicKey,
        IdentityKey recipientPublicKey,
        ReadOnlyMemory<byte> packedWireBytes)
    {
        if (senderPrivateKey.Length != 32) throw new ArgumentException("Sender private key must be 32 bytes.", nameof(senderPrivateKey));
        ArgumentNullException.ThrowIfNull(senderPublicKey);
        ArgumentNullException.ThrowIfNull(recipientPublicKey);

        // 1. Generate fresh ephemeral Curve25519 key pair
        Span<byte> ephPriv = stackalloc byte[32];
        Span<byte> ephPub = stackalloc byte[32];
        RandomNumberGenerator.Fill(ephPriv);
        SignalCryptoEngine.Curve25519Core.X25519BasePoint(ephPub, ephPriv);

        Span<byte> ss1 = stackalloc byte[32];
        Span<byte> ss2 = stackalloc byte[32];
        Span<byte> derived1 = stackalloc byte[64];
        Span<byte> k2 = stackalloc byte[32];
        byte[]? innerPlaintext = null;

        try
        {
            // 2. ss_1 = ECDH(sk_eph, pk_R)
            SignalCryptoEngine.Curve25519Core.X25519(ss1, ephPriv, recipientPublicKey.Span);

            // HKDF-SHA256("UnidentifiedDelivery" || pk_R || pk_eph)
            int infoLen = UnidentifiedDeliveryInfoPrefix.Length + recipientPublicKey.Length + ephPub.Length;
            Span<byte> hkdfInfo1 = stackalloc byte[infoLen];
            UnidentifiedDeliveryInfoPrefix.CopyTo(hkdfInfo1);
            recipientPublicKey.Span.CopyTo(hkdfInfo1[UnidentifiedDeliveryInfoPrefix.Length..]);
            ephPub.CopyTo(hkdfInfo1[(UnidentifiedDeliveryInfoPrefix.Length + recipientPublicKey.Length)..]);

            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ss1,
                derived1,
                salt: default,
                info: hkdfInfo1);

            var chainKey = derived1[..32];
            var k1 = derived1[32..64];

            // 3. Encrypt sender's static public key (c1)
            var output = new byte[HeaderSize + InnerHeaderSize + packedWireBytes.Length];
            output[0] = SealedMagicByte;
            output[1] = Version1;
            ephPub.CopyTo(output.AsSpan(2, 32));

            var nonce1 = output.AsSpan(34, 12);
            RandomNumberGenerator.Fill(nonce1);

            var tag1 = output.AsSpan(46, 16);
            var c1 = output.AsSpan(62, 32);

            using (var aes1 = new AesGcm(k1, TagSize))
            {
                aes1.Encrypt(
                    nonce1,
                    senderPublicKey.Span,
                    c1,
                    tag1,
                    associatedData: output.AsSpan(0, 34)); // Magic (1B) + Version (1B) + pk_eph (32B)
            }

            // 4. ss_2 = ECDH(sk_S, pk_R)
            SignalCryptoEngine.Curve25519Core.X25519(ss2, senderPrivateKey, recipientPublicKey.Span);

            // Advance HKDF-SHA256 with k_chain || c_1 -> k_2
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ss2,
                k2,
                salt: chainKey,
                info: c1);

            // 5. Encrypt inner content (c2)
            innerPlaintext = new byte[InnerHeaderSize + packedWireBytes.Length];
            innerPlaintext[0] = (byte)kind;
            channelId.TryWriteBytes(innerPlaintext.AsSpan(1, 16));
            senderId.TryWriteBytes(innerPlaintext.AsSpan(17, 16));
            BinaryPrimitives.WriteUInt32LittleEndian(innerPlaintext.AsSpan(33, 4), senderDeviceId.Value);
            innerPlaintext.AsSpan(37, 12).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(innerPlaintext.AsSpan(49, 4), (uint)packedWireBytes.Length);
            packedWireBytes.Span.CopyTo(innerPlaintext.AsSpan(InnerHeaderSize));

            var nonce2 = output.AsSpan(94, 12);
            RandomNumberGenerator.Fill(nonce2);

            var tag2 = output.AsSpan(106, 16);
            var c2 = output.AsSpan(HeaderSize);

            using (var aes2 = new AesGcm(k2, TagSize))
            {
                aes2.Encrypt(
                    nonce2,
                    innerPlaintext,
                    c2,
                    tag2,
                    associatedData: c1);
            }

            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephPriv);
            CryptographicOperations.ZeroMemory(ss1);
            CryptographicOperations.ZeroMemory(ss2);
            CryptographicOperations.ZeroMemory(derived1);
            CryptographicOperations.ZeroMemory(k2);
            if (innerPlaintext != null)
            {
                CryptographicOperations.ZeroMemory(innerPlaintext);
            }
        }
    }

    public static DomainResult<(WireFrameKind Kind, ChannelId ChannelId, Domain.Identities.PublicIdentityId SenderId, DeviceId SenderDeviceId, IdentityKey SenderIdentityKey, ReadOnlyMemory<byte> PackedWireBytes)> Decode(
        ReadOnlyMemory<byte> frameBytes,
        ReadOnlySpan<byte> recipientPrivateKey,
        IdentityKey recipientPublicKey)
    {
        if (frameBytes.Length < 2)
        {
            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TOO_SHORT", $"Frame length {frameBytes.Length} is shorter than minimum header size."));
        }

        var span = frameBytes.Span;
        if (span[0] != SealedMagicByte)
        {
            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("UNKNOWN_FRAME_KIND", $"Unknown sealed frame magic: 0x{span[0]:X2}."));
        }

        if (span[1] == VersionGroupBroadcast)
        {
            if (span.Length < GroupHeaderSize)
            {
                return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                    new DomainError("FRAME_TOO_SHORT", $"Group frame length {frameBytes.Length} is shorter than required header size {GroupHeaderSize}."));
            }

            var groupChannelId = ChannelId.FromBytes(span.Slice(2, 16));
            var groupSenderId = Domain.Identities.PublicIdentityId.FromBytes(span.Slice(18, 16));
            uint groupDeviceIdVal = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(34, 4));
            var groupSenderKey = IdentityKey.FromSpan(span.Slice(38, 32));
            uint groupPayloadLen = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(70, 4));

            if (GroupHeaderSize + groupPayloadLen > (uint)span.Length)
            {
                return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                    new DomainError("FRAME_TRUNCATED", "Group frame payload length exceeds available bytes."));
            }

            var groupPackedBytes = frameBytes.Slice(GroupHeaderSize, (int)groupPayloadLen);
            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Success(
                (WireFrameKind.GroupBroadcast, groupChannelId, groupSenderId, new DeviceId(groupDeviceIdVal), groupSenderKey, groupPackedBytes));
        }

        if (span[1] != Version1)
        {
            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("UNKNOWN_FRAME_KIND", $"Unknown sealed frame version: 0x{span[1]:X2}."));
        }

        if (frameBytes.Length < HeaderSize)
        {
            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                new DomainError("FRAME_TOO_SHORT", $"Frame length {frameBytes.Length} is shorter than required header size {HeaderSize}."));
        }

        var ephPub = span.Slice(2, 32);
        var nonce1 = span.Slice(34, 12);
        var tag1 = span.Slice(46, 16);
        var c1 = span.Slice(62, 32);
        var nonce2 = span.Slice(94, 12);
        var tag2 = span.Slice(106, 16);
        var c2 = span.Slice(HeaderSize);

        Span<byte> ss1 = stackalloc byte[32];
        Span<byte> ss2 = stackalloc byte[32];
        Span<byte> derived1 = stackalloc byte[64];
        Span<byte> k2 = stackalloc byte[32];
        Span<byte> senderPubBytes = stackalloc byte[32];
        byte[]? innerPlaintext = null;

        try
        {
            // 1. ss_1 = ECDH(sk_R, pk_eph)
            SignalCryptoEngine.Curve25519Core.X25519(ss1, recipientPrivateKey, ephPub);

            int infoLen = UnidentifiedDeliveryInfoPrefix.Length + recipientPublicKey.Length + ephPub.Length;
            Span<byte> hkdfInfo1 = stackalloc byte[infoLen];
            UnidentifiedDeliveryInfoPrefix.CopyTo(hkdfInfo1);
            recipientPublicKey.Span.CopyTo(hkdfInfo1[UnidentifiedDeliveryInfoPrefix.Length..]);
            ephPub.CopyTo(hkdfInfo1[(UnidentifiedDeliveryInfoPrefix.Length + recipientPublicKey.Length)..]);

            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ss1,
                derived1,
                salt: default,
                info: hkdfInfo1);

            var chainKey = derived1[..32];
            var k1 = derived1[32..64];

            // 2. Decrypt sender's static public key
            try
            {
                using var aes1 = new AesGcm(k1, TagSize);
                aes1.Decrypt(nonce1, c1, tag1, senderPubBytes, associatedData: span.Slice(0, 34));
            }
            catch (CryptographicException)
            {
                return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                    new DomainError("FRAME_AUTHENTICATION_FAILED", "Failed to decrypt sender identity from sealed frame."));
            }

            var senderIdentityKey = IdentityKey.FromSpan(senderPubBytes);

            // 3. ss_2 = ECDH(sk_R, pk_S)
            SignalCryptoEngine.Curve25519Core.X25519(ss2, recipientPrivateKey, senderPubBytes);

            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ss2,
                k2,
                salt: chainKey,
                info: c1);

            // 4. Decrypt inner content
            innerPlaintext = new byte[c2.Length];
            try
            {
                using var aes2 = new AesGcm(k2, TagSize);
                aes2.Decrypt(nonce2, c2, tag2, innerPlaintext, associatedData: c1);
            }
            catch (CryptographicException)
            {
                return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                    new DomainError("FRAME_AUTHENTICATION_FAILED", "Failed to decrypt inner content from sealed frame."));
            }

            var kind = (WireFrameKind)innerPlaintext[0];
            var channelId = ChannelId.FromBytes(innerPlaintext.AsSpan(1, 16));
            var senderId = Domain.Identities.PublicIdentityId.FromBytes(innerPlaintext.AsSpan(17, 16));
            uint deviceVal = BinaryPrimitives.ReadUInt32LittleEndian(innerPlaintext.AsSpan(33, 4));
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(innerPlaintext.AsSpan(49, 4));

            if (InnerHeaderSize + payloadLength > innerPlaintext.Length)
            {
                return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Failure(
                    new DomainError("FRAME_TRUNCATED", "Inner payload length exceeds decrypted frame buffer."));
            }

            var packedPayload = new byte[(int)payloadLength];
            Array.Copy(innerPlaintext, InnerHeaderSize, packedPayload, 0, (int)payloadLength);

            return DomainResult<(WireFrameKind, ChannelId, Domain.Identities.PublicIdentityId, DeviceId, IdentityKey, ReadOnlyMemory<byte>)>.Success(
                (kind, channelId, senderId, new DeviceId(deviceVal), senderIdentityKey, packedPayload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ss1);
            CryptographicOperations.ZeroMemory(ss2);
            CryptographicOperations.ZeroMemory(derived1);
            CryptographicOperations.ZeroMemory(k2);
            CryptographicOperations.ZeroMemory(senderPubBytes);
            if (innerPlaintext != null)
            {
                CryptographicOperations.ZeroMemory(innerPlaintext);
            }
        }
    }
}
