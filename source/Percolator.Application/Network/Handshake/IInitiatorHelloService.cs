namespace Percolator.Application.Network.Handshake;

public interface IInitiatorHelloService
{
    Task SendInitiatorHelloViaHostAsync(
        byte[] recipientPublicKeyHash,
        byte[] remoteIdentityKeySpki,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        byte[] remotePreKeySpki,
        Percolator.Identity.PeerId hostPeerId,
        byte[]? initiatorPayload,
        CancellationToken cancellationToken);
}