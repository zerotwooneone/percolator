namespace Percolator.Application.Apps.Chat
{
    public interface IActingAdminResolver
    {
        Task<Guid?> GetActingAdminPeerIdAsync(Guid conversationId, CancellationToken ct);
    }
}
