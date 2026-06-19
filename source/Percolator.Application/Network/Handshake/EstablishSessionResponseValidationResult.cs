using Percolator.Cryptography;

namespace Percolator.Application.Network.Handshake;

/// <summary>
/// Result of validating an EstablishSessionResponse.
/// Contains the parsed session ID and remote identity information needed for finalization.
/// </summary>
public sealed class EstablishSessionResponseValidationResult
{
    public SessionId SessionId { get; }
    public byte[] RemoteIdentitySpki { get; }
    public byte[] RemotePublicKeyHash { get; }

    public EstablishSessionResponseValidationResult(
        SessionId sessionId,
        byte[] remoteIdentitySpki,
        byte[] remotePublicKeyHash)
    {
        SessionId = sessionId;
        RemoteIdentitySpki = remoteIdentitySpki;
        RemotePublicKeyHash = remotePublicKeyHash;
    }
}
