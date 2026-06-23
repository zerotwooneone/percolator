using Percolator.SourceGenerators;

namespace Percolator.Chat.GroupLedger;

[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record CiphertextBytes;
