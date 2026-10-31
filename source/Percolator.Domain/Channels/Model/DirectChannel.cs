using Percolator.Domain.Channels.Events;
using System.ComponentModel;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Channels.Model;

public sealed class DirectChannel : AggregateRoot<ChannelId>
{
    public const int MaxRetainedPayloads = 1000;

    public override ChannelId Id { get; }
    public PublicIdentityId OwnerIdentityId { get; }
    public PublicIdentityId RemotePeerId { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    private readonly List<ChannelPayload> _payloads = [];
    private readonly HashSet<PayloadId> _payloadIds = [];
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

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DirectChannel Rehydrate(
        ChannelId id,
        PublicIdentityId ownerIdentityId,
        PublicIdentityId remotePeerId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastActivityUtc,
        IEnumerable<ChannelPayload>? payloads = null)
    {
        if (!id.IsValid) throw new ArgumentException("ChannelId cannot be empty.", nameof(id));
        if (!ownerIdentityId.IsValid) throw new ArgumentException("OwnerIdentityId cannot be empty.", nameof(ownerIdentityId));
        if (!remotePeerId.IsValid) throw new ArgumentException("RemotePeerId cannot be empty.", nameof(remotePeerId));
        if (ownerIdentityId == remotePeerId) throw new ArgumentException("Owner cannot establish a direct channel with self.", nameof(remotePeerId));

        var channel = new DirectChannel(id, ownerIdentityId, remotePeerId, createdAtUtc)
        {
            LastActivityUtc = lastActivityUtc
        };
        if (payloads != null)
        {
            foreach (var payload in payloads)
            {
                ArgumentNullException.ThrowIfNull(payload);
                if (!channel._payloadIds.Add(payload.Id))
                {
                    throw new ArgumentException($"Duplicate payload with ID {payload.Id} cannot be rehydrated.", nameof(payloads));
                }
                channel._payloads.Add(payload);
            }

            if (channel._payloads.Count > MaxRetainedPayloads)
            {
                throw new ArgumentException($"DirectChannel cannot retain more than {MaxRetainedPayloads} payloads.", nameof(payloads));
            }
        }
        return channel;
    }

    public DomainResult AppendPayload(ChannelPayload payload, IDateTimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!payload.Id.IsValid)
        {
            return DomainResult.Failure(new DomainError("INVALID_PAYLOAD_ID", "Payload identifier cannot be empty."));
        }

        if (payload.ChannelId != Id)
        {
            return DomainResult.Failure(new DomainError("CHANNEL_MISMATCH", "Payload does not belong to this channel."));
        }

        if (payload.AuthorId != OwnerIdentityId && payload.AuthorId != RemotePeerId)
        {
            return DomainResult.Failure(new DomainError("SENDER_NOT_PARTICIPANT", "Payload author is not a participant in this direct channel."));
        }

        if (!_payloadIds.Add(payload.Id))
        {
            return DomainResult.Failure(new DomainError("DUPLICATE_PAYLOAD_ID", "A payload with this identifier has already been appended to the channel."));
        }

        while (_payloads.Count >= MaxRetainedPayloads)
        {
            var oldest = _payloads[0];
            _payloads.RemoveAt(0);
            _payloadIds.Remove(oldest.Id);
        }

        _payloads.Add(payload);
        LastActivityUtc = timeProvider.UtcNow;

        AddDomainEvent(new PayloadAppendedEvent(Id, payload.Id, LastActivityUtc));

        return DomainResult.Success();
    }
}
