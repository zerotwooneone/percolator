using System;

namespace Percolator.Infrastructure.Persistence;

public class GroupMemberDbo
{
    public Guid ConversationId { get; set; }
    public byte[] MemberSpki { get; set; } = Array.Empty<byte>();
    public byte[] MemberSpkiHash { get; set; } = Array.Empty<byte>();
    public int Role { get; set; } // 0 = Member, 1 = Admin
    public long JoinedAtSequence { get; set; }
}
