using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Channels.Model;

public sealed class ChannelPayload : IEntity<PayloadId>
{
    public PayloadId Id { get; }
    public ChannelId ChannelId { get; }
    public PublicIdentityId AuthorId { get; }
    public DeviceId AuthorDeviceId { get; }
    public ReadOnlyMemory<byte> EncryptedPayload { get; }
    public DateTimeOffset SentAtUtc { get; }

    public ChannelPayload(
        PayloadId id,
        ChannelId channelId,
        PublicIdentityId authorId,
        DeviceId authorDeviceId,
        ReadOnlyMemory<byte> encryptedPayload,
        DateTimeOffset sentAtUtc)
    {
        Id = id;
        ChannelId = channelId;
        AuthorId = authorId;
        AuthorDeviceId = authorDeviceId;
        EncryptedPayload = encryptedPayload;
        SentAtUtc = sentAtUtc;
    }
}
