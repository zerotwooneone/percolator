using Percolator.SourceGenerators;

namespace Percolator.Domain.Security.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct SessionId;
