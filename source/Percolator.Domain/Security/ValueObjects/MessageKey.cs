using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[ByteArray(length: 32)]
public sealed partial record MessageKey;
