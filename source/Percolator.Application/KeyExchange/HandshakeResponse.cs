using Percolator.Cryptography;
using System.Security.Cryptography;

namespace Percolator.Application.KeyExchange;

public record HandshakeResponse(
    SharedSecret SharedSecret,
    Percolator.Contracts.PreKeyBundle ResponderBundle,
    ECDiffieHellman ResponderPrivateKeyUsed
);
