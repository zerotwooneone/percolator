using Percolator.SourceGenerators;

namespace Percolator.Domain.Conversations.ValueObjects;

[GuidId(GuidIdKind.SequentialTimeBased)]
public readonly partial record struct MessageId;
