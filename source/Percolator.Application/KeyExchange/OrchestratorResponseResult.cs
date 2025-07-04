using Percolator.Cryptography;
using Percolator.Contracts;
using Percolator.Sessions;

namespace Percolator.Application.KeyExchange;

public record OrchestratorResponseResult(
    SharedSecret SharedSecret,
    OpaquePublicKey InitialRatchetPublicKey,
    Percolator.Contracts.PreKeyBundle ResponderBundle
);
