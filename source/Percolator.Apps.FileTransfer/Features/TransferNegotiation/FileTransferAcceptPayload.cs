using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferAcceptPayload(
    TransferSessionId TransferSessionId,
    IReadOnlyList<DirectEndpointCandidate> ReceiverCandidates,
    TransportRoutingPreference RoutingPreference,
    Bitfield ResumptionBitfield);
