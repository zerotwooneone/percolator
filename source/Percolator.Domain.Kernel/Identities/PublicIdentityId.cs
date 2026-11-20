using Percolator.SourceGenerators;

namespace Percolator.Domain.Identities;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct PublicIdentityId;
