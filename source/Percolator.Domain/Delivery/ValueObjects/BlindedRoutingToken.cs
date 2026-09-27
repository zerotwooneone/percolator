using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct BlindedRoutingToken;
