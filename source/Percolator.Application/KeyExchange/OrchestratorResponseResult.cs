using Percolator.Cryptography;

namespace Percolator.Application.KeyExchange;

public record OrchestratorResponseResult(
    SharedSecret SharedSecret,
    Percolator.Contracts.PreKeyBundle ResponderBundle
);
