using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record EncryptedEntriesBlobBytes;
