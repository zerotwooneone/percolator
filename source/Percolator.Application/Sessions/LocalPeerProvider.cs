using Percolator.Sessions;

namespace Percolator.Application.Sessions;

/// <summary>
/// This is a placeholder implementation. In a real application, this would
/// likely retrieve the local peer's identity from a secure configuration store
/// or the Identity domain.
/// </summary>
public class LocalPeerProvider : ILocalPeerProvider
{
    // A static, hardcoded PeerId is used for now.
    private static readonly PeerId LocalPeer = new(new Guid("00000000-0000-0000-0000-000000000001"));

    public Task<PeerId> GetPeerIdAsync() => Task.FromResult(LocalPeer);
}
