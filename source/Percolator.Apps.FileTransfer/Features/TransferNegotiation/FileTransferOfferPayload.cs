using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferOfferPayload(
    TransferSessionId TransferSessionId,
    TransferBundleSummary Summary,
    ManifestDistribution Manifest,
    SwarmRendezvousId SwarmRendezvousId,
    StreamAuthToken StreamAuthToken,
    RelayRendezvousDescriptor? RelayRendezvous,
    IReadOnlyList<DirectEndpointCandidate> DirectCandidates,
    CertSha256Fingerprint? SenderCertSha256 = null);
