namespace Percolator.Application.Network.Handshake;

public interface IInitiatorHelloService
{
    Task SendInitiatorHelloViaHostAsync(
        byte[] recipientPublicKeyHash,
        Percolator.Cryptography.PreKeyBundle remoteBundle,
        Guid signedPreKeyId,
        Guid? oneTimePreKeyId,
        Percolator.Identity.PeerId hostPeerId,
        byte[]? initiatorPayload,
        CancellationToken cancellationToken);
}