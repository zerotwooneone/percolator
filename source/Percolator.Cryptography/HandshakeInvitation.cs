using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record HandshakeInvitation(byte[] Value) : ByteArrayRecord(Value);
