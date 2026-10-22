using System.Buffers.Binary;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class FakeSessionWirePacker : ISessionWirePacker
{
    public ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> ciphertext)
    {
        // Header: EphemeralPublicKey (32) + Counter (4) + PreviousChainLength (4) + Ciphertext
        var result = new byte[40 + ciphertext.Length];
        header.EphemeralPublicKey.Span.CopyTo(result.AsSpan(0, 32));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), header.Counter);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), header.PreviousChainLength);
        ciphertext.Span.CopyTo(result.AsSpan(40));
        return result;
    }

    public ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext)
    {
        // Iteration (4) + Signature (64) + Ciphertext
        var result = new byte[4 + signature.Length + ciphertext.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), iteration);
        signature.Span.CopyTo(result.AsSpan(4, signature.Length));
        ciphertext.Span.CopyTo(result.AsSpan(4 + signature.Length));
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
        if (wireBytes.Length < 40)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short"));
        }

        var keySpan = wireBytes.Slice(0, 32).Span;
        if (!DhPublicKey.TryFromSpan(keySpan, out var dhKey) || dhKey is null)
        {
            return DomainResult<InboundDirectEnvelope>.Failure(new DomainError("INVALID_KEY", "Invalid DH key"));
        }

        uint counter = BinaryPrimitives.ReadUInt32LittleEndian(wireBytes.Slice(32, 4).Span);
        uint previousChainLength = BinaryPrimitives.ReadUInt32LittleEndian(wireBytes.Slice(36, 4).Span);
        var header = new RatchetHeader(dhKey, counter, previousChainLength);
        var ciphertext = wireBytes.Slice(40);

        return DomainResult<InboundDirectEnvelope>.Success(new InboundDirectEnvelope(
            channelId,
            recipientId,
            senderId,
            senderDeviceId,
            header,
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
        if (wireBytes.Length < 68)
        {
            return DomainResult<InboundGroupEnvelope>.Failure(new DomainError("CORRUPT_WIRE", "Payload too short for iteration and signature"));
        }

        uint iteration = BinaryPrimitives.ReadUInt32LittleEndian(wireBytes.Slice(0, 4).Span);
        var sig = wireBytes.Slice(4, 64);
        var ciphertext = wireBytes.Slice(68);

        return DomainResult<InboundGroupEnvelope>.Success(new InboundGroupEnvelope(
            channelId,
            recipientId,
            authorId,
            authorDeviceId,
            iteration,
            ciphertext,
            sig,
            receivedAtUtc));
    }
}
