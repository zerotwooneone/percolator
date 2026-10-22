using System.Threading;
using System.Threading.Tasks;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Extends ILocalIdentityKeyStore with registration/saving capability for local desktop cryptographic keys.
/// </summary>
public interface IDesktopIdentityKeyStore : ILocalIdentityKeyStore
{
    Task SaveKeyAsync(PublicIdentityId identityId, byte[] privateKey, IdentityKey publicKey, CancellationToken ct = default);
}
