namespace Percolator.Application.Apps.Chat
{
    // Resolves the active recipient PublicKeyHash (PKH) for a peer
    public interface IRecipientPkhResolver
    {
        Task<byte[]?> GetActivePkhAsync(Guid peerId, CancellationToken ct);
    }
}
