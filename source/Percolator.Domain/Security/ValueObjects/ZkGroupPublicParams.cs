using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[ByteArray(minLength: 1, maxLength: 500)]
public sealed partial record ZkGroupPublicParams;
