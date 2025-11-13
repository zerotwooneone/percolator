using Percolator.Network.Primitives;

namespace Percolator.Network.ValueObjects;

public record IdentityPublicKey(byte[] Value) : ByteArrayRecord(Value);
