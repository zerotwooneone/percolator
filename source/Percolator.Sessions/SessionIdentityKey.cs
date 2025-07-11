using Percolator.Sessions.Primitives;

namespace Percolator.Sessions;

public record SessionIdentityKey(byte[] Value) : ByteArrayRecord(Value);