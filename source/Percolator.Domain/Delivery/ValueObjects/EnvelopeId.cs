using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

[GuidId(GuidIdKind.SequentialTimeBased)]
public readonly partial record struct EnvelopeId;
