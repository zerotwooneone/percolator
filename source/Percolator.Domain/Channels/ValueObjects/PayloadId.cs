using Percolator.SourceGenerators;

namespace Percolator.Domain.Channels.ValueObjects;

[GuidId(GuidIdKind.SequentialTimeBased)]
public readonly partial record struct PayloadId;
