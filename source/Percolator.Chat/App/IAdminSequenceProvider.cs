namespace Percolator.Chat.App
{
    // Issues a monotonic sequence number for admin operations per group conversation
    public interface IAdminSequenceProvider
    {
        Task<ulong> NextAsync(Guid groupConversationGuid, CancellationToken ct = default);
    }
}
