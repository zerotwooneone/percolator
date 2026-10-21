using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferCancelPayload(
    TransferSessionId TransferSessionId,
    TransferTerminationReason Reason);
