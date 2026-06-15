using Percolator.Cryptography;

namespace Percolator.Application.Chat;

public interface ILocalIdentitySigner
{
    Task<Signature> SignWithRelayRootKeyAsync(byte[] payload, CancellationToken ct);
    Task<Signature> SignWithLocalIdentityKeyAsync(byte[] payload, CancellationToken ct);
}
