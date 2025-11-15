using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

// Renamed to avoid conflict with Percolator.Application.KeyExchange.HandshakeResponse
public record HandshakeResponseMessage(byte[] Value) : ByteArrayRecord(Value);
