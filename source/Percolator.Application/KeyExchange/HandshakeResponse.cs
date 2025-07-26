using Percolator.Cryptography;

namespace Percolator.Application.KeyExchange;

public record HandshakeResponse(
    SharedSecret SharedSecret,
    Percolator.Contracts.PreKeyBundle ResponderBundle
);
