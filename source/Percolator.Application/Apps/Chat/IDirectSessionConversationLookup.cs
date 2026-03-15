namespace Percolator.Application.Apps.Chat
{
    // Maps a group conversation to the direct session used as the transport for key envelopes
    public interface IDirectSessionConversationLookup
    {
        Task<Guid?> GetDirectSessionIdAsync(Guid conversationId, int selfIdentityId, CancellationToken ct);
        Task<Guid?> GetDirectSessionIdAsync(Guid conversationId, int selfIdentityId, Guid remotePeerId, CancellationToken ct);
    }
}
