using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakeSessionWirePacker : ISessionWirePacker
{
    public ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> nonce, ReadOnlyMemory<byte> ciphertext)
    {
        // Simple concatenation: header key (32) + nonce (12) + ciphertext
        var result = new byte[32 + nonce.Length + ciphertext.Length];
        header.EphemeralPublicKey.Span.CopyTo(result.AsSpan(0, 32));
        nonce.Span.CopyTo(result.AsSpan(32, nonce.Length));
        ciphertext.Span.CopyTo(result.AsSpan(32 + nonce.Length));
        return result;
    }

    public ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext)
    {
        var result = new byte[signature.Length + ciphertext.Length];
        signature.Span.CopyTo(result.AsSpan(0, signature.Length));
        ciphertext.Span.CopyTo(result.AsSpan(signature.Length));
        return result;
    }

    public DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId senderId,
        DeviceId senderDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.Length < 44)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short"));
        }

        var keySpan = wireBytes.Slice(0, 32).Span;
        if (!DhPublicKey.TryFromSpan(keySpan, out var dhKey) || dhKey is null)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("INVALID_KEY", "Invalid DH key"));
        }

        var header = new RatchetHeader(dhKey, 0, 0);
        var nonce = wireBytes.Slice(32, 12);
        var ciphertext = wireBytes.Slice(44);

        return DomainResult<InboundDirectEnvelope>.Success(new InboundDirectEnvelope(
            channelId,
            recipientId,
            senderId,
            senderDeviceId,
            header,
            nonce,
            ciphertext,
            receivedAtUtc));
    }

    public DomainResult<InboundGroupEnvelope> UnpackGroupMessage(
        ChannelId channelId,
        PublicIdentityId recipientId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ReadOnlyMemory<byte> wireBytes,
        DateTimeOffset receivedAtUtc)
    {
        if (wireBytes.Length < 64)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short for signature"));
        }

        var sig = wireBytes.Slice(0, 64);
        var ciphertext = wireBytes.Slice(64);

        return DomainResult<InboundGroupEnvelope>.Success(new InboundGroupEnvelope(
            channelId,
            recipientId,
            authorId,
            authorDeviceId,
            0,
            ciphertext,
            sig,
            receivedAtUtc));
    }
}
