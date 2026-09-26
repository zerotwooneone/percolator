using Percolator.Domain.Common;
using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Conversations.Model;

public sealed class GroupMember : IEntity<PublicIdentityId>
{
    public PublicIdentityId Id { get; }
    public GroupRole Role { get; private set; }
    public DateTimeOffset JoinedAtUtc { get; }

    public GroupMember(PublicIdentityId id, GroupRole role, DateTimeOffset joinedAtUtc)
    {
        Id = id;
        Role = role;
        JoinedAtUtc = joinedAtUtc;
    }

    public void ChangeRole(GroupRole newRole)
    {
        Role = newRole;
    }
}
