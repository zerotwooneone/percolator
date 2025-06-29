namespace Percolator.Cryptography;

public record HandshakeInitiationResult(SharedSecret SharedSecret, PublicKey EphemeralPublicKey);
