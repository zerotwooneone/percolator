using System.Security.Cryptography;
using Percolator.Cryptography;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.KeyExchange;

public interface IX3DHOrchestrator
{
    SharedSecret InitiateHandshake(ContractsPreKeyBundle remotePreKeyBundle, ECDiffieHellman ephemeralKey);

    HandshakeResponse CompleteHandshake(
        ContractsPreKeyBundle remotePreKeyBundle, 
        byte[] remoteEphemeralPublicKey,
        ECDiffieHellman? localOneTimePreKey);
}
