namespace Percolator.Cryptography;

// Port placeholder for cryptographic operations used by domain aggregates.
// Expanded via TDD to include handshake response creation.
public interface ICryptoPrimitives
{
    HandshakeResponseMessage CreateHandshakeResponse(HandshakeInvitation invitation, IKeyStore keyStore);
}
