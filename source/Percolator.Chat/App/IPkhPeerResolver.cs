using System.Threading;
using System.Threading.Tasks;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.App;

/// <summary>
/// Abstraction to resolve a PKH to a local peer identifier without
/// directly referencing Identity layer types from the Chat project.
/// The Application layer will provide an adapter implementation using
/// IPeerPublicSigningKeyStore.
/// </summary>
public interface IPkhPeerResolver
{
    Task<int?> GetPeerIdByPkhAsync(Pkh pkh, CancellationToken cancellationToken);
}
