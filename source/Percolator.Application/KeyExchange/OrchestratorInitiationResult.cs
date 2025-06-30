using Percolator.Cryptography;
using Percolator.Sessions;
using ContractsPreKeyBundle = Percolator.Contracts.PreKeyBundle;

namespace Percolator.Application.KeyExchange;

public record OrchestratorInitiationResult(
    SharedSecret SharedSecret,
    OpaquePublicKey InitialRatchetPublicKey,
    ContractsPreKeyBundle LocalPreKeyBundle
);
