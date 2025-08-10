using Percolator.Cryptography;
using System.Security.Cryptography;

namespace Percolator.Application.KeyExchange;

public record HandshakeResponse(
    SharedSecret SharedSecret,
    X3dPreKeyBundle ResponderBundle,
    ECDiffieHellman ResponderPrivateKeyUsed
);
