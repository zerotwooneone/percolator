namespace Percolator.Application.Apps.Chat
{
    public interface IAdminOperationSigner
    {
        Task<(byte[] payload, byte[] signature)> SignGrantAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, byte[] granteeSpki, ulong? adminSequenceNumber = null, CancellationToken ct = default);
        Task<(byte[] payload, byte[] signature)> SignRevokeAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, byte[] granteeSpki, ulong? adminSequenceNumber = null, CancellationToken ct = default);
        Task<(byte[] payload, byte[] signature)> SignUpdateMembershipAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, IReadOnlyList<Guid>? membersToAdd, IReadOnlyList<Guid>? membersToRemove, bool? leaveGroup, ulong? adminSequenceNumber = null, CancellationToken ct = default);
        Task<(byte[] payload, byte[] signature)> SignUpdateInfoAsync(Guid groupConversationId, Guid opId, DateTimeOffset sentUtc, string? newGroupName, byte[]? newGroupAvatar, ulong? adminSequenceNumber = null, CancellationToken ct = default);
    }
}
