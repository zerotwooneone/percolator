namespace Percolator.Chat.App
{
    public sealed record GroupAdminState(ulong NextAdminSequenceNumber, uint LastCommittedKeyVersion);

    public interface IGroupAdminStateStore
    {
        Task<GroupAdminState?> GetAsync(Guid conversationId, CancellationToken ct);
        Task InitializeIfMissingAsync(Guid conversationId, CancellationToken ct);
        Task<bool> TryCommitAsync(Guid conversationId, ulong expectedAdminSequenceNumber, uint committedKeyVersion, CancellationToken ct);
    }
}
