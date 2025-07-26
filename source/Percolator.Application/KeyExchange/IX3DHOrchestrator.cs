using System.Security.Cryptography;
using Percolator.Cryptography;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.KeyExchange;

public interface IX3DHOrchestrator
{
    SharedSecret CompleteHandshake(ContractsPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey);

    HandshakeResponse ProcessHandshake(ContractsPreKeyBundle remotePreKeyBundle,
        byte[] remoteEphemeralPublicKey);
}
