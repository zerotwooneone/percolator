using Percolator.Domain.Channels.Events;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Model;

public sealed class DirectChannel : AggregateRoot<ChannelId>
{
    public override ChannelId Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    private readonly List<ChannelPayload> _payloads = [];
    public IReadOnlyList<ChannelPayload> Payloads => _payloads.AsReadOnly();

    private DirectChannel(
        ChannelId id,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        OwnerIdentityId = ownerIdentityId;
        RemotePeerId = remotePeerId;
        CreatedAtUtc = createdAtUtc;
        LastActivityUtc = createdAtUtc;
    }

    public static DomainResult<DirectChannel> Create(
        ChannelId id,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        IDateTimeProvider timeProvider)
    {
        if (!id.IsValid)
        {
            return DomainResult<DirectChannel>.Failure(new DomainError("INVALID_CHANNEL_ID", "ChannelId cannot be empty."));
        }

        if (!ownerIdentityId.IsValid)
        {
            return DomainResult<DirectChannel>.Failure(new DomainError("INVALID_OWNER_ID", "OwnerIdentityId cannot be empty."));
        }

        if (!remotePeerId.IsValid)
        {
            return DomainResult<DirectChannel>.Failure(new DomainError("INVALID_PEER_ID", "RemotePeerId cannot be empty."));
        }

        if (ownerIdentityId == remotePeerId)
        {
            return DomainResult<DirectChannel>.Failure(new DomainError("SELF_CHANNEL_NOT_ALLOWED", "Owner cannot establish a direct channel with self."));
        }

        var channel = new DirectChannel(id, ownerIdentityId, remotePeerId, timeProvider.UtcNow);
        return DomainResult<DirectChannel>.Success(channel);
    }

    public DomainResult AppendPayload(ChannelPayload payload, IDateTimeProvider timeProvider)
    {
        if (payload.ChannelId != Id)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Payload does not belong to this channel."));
        }

        if (payload.AuthorId != OwnerIdentityId && payload.AuthorId != RemotePeerId)
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_PARTICIPANT", "Payload author is not a participant in this direct channel."));
        }

        _payloads.Add(payload);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new PayloadAppendedEvent(Id, payload.Id, LastActivityUtc));

        return DomainResult.Success();
    }
}
