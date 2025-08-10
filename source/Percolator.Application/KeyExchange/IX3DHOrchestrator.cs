using System.Security.Cryptography;
using Percolator.Cryptography;

namespace Percolator.Application.KeyExchange;

public interface IX3DHOrchestrator
{
    SharedSecret InitiateHandshake(X3dPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey);

    HandshakeResponse CompleteHandshake(
        X3dPreKeyBundle remotePreKeyBundle, 
        byte[] remoteEphemeralPublicKey,
        ECDiffieHellman? localOneTimePreKey);
}
