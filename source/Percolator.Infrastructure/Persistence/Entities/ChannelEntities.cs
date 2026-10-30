namespace Percolator.Infrastructure.Persistence.Entities;

using System;

public sealed class ChatChannelEntity
{
    public Guid Id { get; set; }
    public int Type { get; set; } // 0 = Direct, 1 = Group
    public string Name { get; set; } = string.Empty;
    public Guid? OwnerIdentityId { get; set; }
    public Guid? RemotePeerId { get; set; }
    public uint? CurrentEpoch { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }
}

public sealed class ChatChannelMemberEntity
{
    public Guid ChannelId { get; set; }
    public Guid MemberIdentityId { get; set; }
    public int Role { get; set; } // 0 = Admin, 1 = Member
    public DateTimeOffset JoinedAtUtc { get; set; }
}

public sealed class ChatChannelPayloadEntity
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public Guid AuthorId { get; set; }
    public uint AuthorDeviceId { get; set; }
    public byte[] EncryptedPayload { get; set; } = Array.Empty<byte>();
    public DateTimeOffset SentAtUtc { get; set; }
}
