using System.Security.Cryptography;
using Percolator.Cryptography;

namespace Percolator.Application.KeyExchange;

public interface IX3DHOrchestrator
{
    SharedSecret InitiateHandshake(X3dPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey);

    HandshakeResponse CompleteHandshake(
        RatchetIdentityKey remoteIdentityKey,
        RatchetEphemeralKey remoteEphemeralKey,
        ECDiffieHellman? localOneTimePreKey);
}
