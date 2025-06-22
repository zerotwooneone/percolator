namespace Percolator.Identity;

public record PeerIdentity(byte[] IdentityKey, byte[] PreKeyBundle);

public interface IPeerIdentityStore
{
    Task StorePeerAsync(PeerIdentity peer);
    Task<PeerIdentity?> GetPeerAsync(byte[] identityKey);
}
