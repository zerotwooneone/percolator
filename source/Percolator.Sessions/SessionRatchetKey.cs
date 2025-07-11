using Percolator.Sessions.Primitives;

namespace Percolator.Sessions;

public record SessionRatchetKey(byte[] Value) : ByteArrayRecord(Value);