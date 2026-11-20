using Percolator.SourceGenerators;

namespace Percolator.Domain.Channels.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct ChannelId;
