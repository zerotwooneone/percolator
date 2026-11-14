using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Domain.Channels.Model;

public sealed class ChannelMember : IEntity<PublicIdentityId>
{
    public PublicIdentityId Id { get; }
    public ChannelRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; }

    public ChannelMember(PublicIdentityId id, ChannelRole role, DateTimeOffset joinedAtUtc)
    {
        Id = id;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
    }

    public void ChangeRole(ChannelRole newRole)
    {
        Role = newRole;
    }
}
