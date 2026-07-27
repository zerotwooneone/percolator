using Percolator.SourceGenerators;

namespace Percolator.Chat.ValueObjects;

[ByteArray(minLength: 1, maxLength: 5000)]
public sealed partial record EncryptedGroupProfileBytes;
