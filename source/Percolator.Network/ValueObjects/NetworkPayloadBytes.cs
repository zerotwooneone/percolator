using Percolator.SourceGenerators;

namespace Percolator.Network.ValueObjects;

[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record NetworkPayloadBytes;
