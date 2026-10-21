using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferOfferPayload(
    TransferSessionId TransferSessionId,
    TransferBundleSummary Summary,
    ManifestDistribution Manifest,
    TransferKey SessionEncryptionKey,
    BaseNonce BaseNonce,
    StreamAuthToken StreamAuthToken,
    RelayRendezvousDescriptor? RelayRendezvous,
    IReadOnlyList<DirectEndpointCandidate> DirectCandidates);
