namespace Percolator.Application.Apps.Chat
{
    public interface IAtRestKeyProvider
    {
        Task<byte[]> GetMasterKeyAsync(CancellationToken ct);
    }
}
