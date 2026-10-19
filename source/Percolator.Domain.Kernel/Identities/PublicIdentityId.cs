using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities.ValueObjects;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct PublicIdentityId;
