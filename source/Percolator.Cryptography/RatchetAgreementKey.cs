using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record RatchetAgreementKey(byte[] Value) : ByteArrayRecord(Value);