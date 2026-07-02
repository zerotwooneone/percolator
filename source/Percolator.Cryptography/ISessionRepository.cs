namespace Percolator.Cryptography;

public interface ISessionRepository
{
    Task AddAsync(SecureSession session, CancellationToken cancellationToken = default);
    Task<SecureSession?> GetAsync(SessionId id, CancellationToken cancellationToken = default);
    Task UpdateAsync(SecureSession session, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecureSession>> GetAllActiveAsync(uint selfIdentityId, CancellationToken cancellationToken = default);
}
