using Percolator.Cryptography.Primitives;

namespace Percolator.Cryptography;

public record PrivateAgreementKey(byte[] Value) : ByteArrayRecord(Value);