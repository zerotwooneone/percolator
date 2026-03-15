namespace Percolator.Application.Apps.Chat
{
    public interface IGroupManagerStateStore
    {
        Task<byte[]?> GetAsync(Guid conversationId, CancellationToken ct);
        Task SaveAsync(Guid conversationId, byte[] stateBlob, DateTimeOffset updatedAtUtc, CancellationToken ct);
    }
}
