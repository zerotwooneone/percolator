using Percolator.SourceGenerators;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

[GuidId(GuidIdKind.CryptographicRandom)]
public readonly partial record struct TransferSessionId;
