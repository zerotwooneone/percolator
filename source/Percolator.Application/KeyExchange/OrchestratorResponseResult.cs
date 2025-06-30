using Percolator.Cryptography;
using Percolator.Sessions;

namespace Percolator.Application.KeyExchange;

public record OrchestratorResponseResult(
    SharedSecret SharedSecret,
    OpaquePublicKey InitialRatchetPublicKey
);
