using Percolator.SourceGenerators;

namespace Percolator.Domain.Conversations.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct ConversationId;
