using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

// Renamed to avoid conflict with Percolator.Application.KeyExchange.HandshakeResponse
[ByteArray(minLength: 1, maxLength: 10000)]
public sealed partial record HandshakeResponseMessage;
