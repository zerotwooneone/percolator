using Percolator.Application2.Ingress;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;

namespace Percolator.Application2.Ports;

public interface ISessionWirePacker
{
    ReadOnlyMemory<byte> PackDirectRatchetMessage(RatchetHeader header, ReadOnlyMemory<byte> nonce, ReadOnlyMemory<byte> ciphertext);
    ReadOnlyMemory<byte> PackGroupMessage(ChannelId channelId, uint iteration, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> ciphertext);
    DomainResult<InboundDirectEnvelope> UnpackDirectRatchetMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId senderId, DeviceId senderDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
    DomainResult<InboundGroupEnvelope> UnpackGroupMessage(ChannelId channelId, PublicIdentityId recipientId, PublicIdentityId authorId, DeviceId authorDeviceId, ReadOnlyMemory<byte> wireBytes, DateTimeOffset receivedAtUtc);
}
