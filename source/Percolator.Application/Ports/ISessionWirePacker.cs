using Percolator.Application.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Application.Ports;

public interface ISessionWirePacker
{
    ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> ciphertext);
    ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext);
    ReadOnlyMemory<byte> PackHandshakeMessage(HandshakeHeader header, ReadOnlyMemory<byte> encryptedPayload);

    DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
    DomainResult<InboundGroupEnvelope> UnpackGroupMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId authorId, DeviceId authorDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
    DomainResult<InboundHandshakeEnvelope> UnpackHandshakeMessage(PublicIdentityId recipientId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
}
