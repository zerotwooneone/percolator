using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

[ByteArray(minLength: 1, maxLength: 4096)]
public sealed partial record SenderKeyRecordBytes;
