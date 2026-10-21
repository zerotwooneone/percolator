using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public sealed record FileTransferCancelPayload(
    TransferSessionId TransferSessionId,
    TransferTerminationReason Reason)
{
    public static FileTransferCancelPayload Deserialize(ReadOnlyMemory<byte> bytes) =>
        FileTransferPayloadSerializer.DeserializeCancel(bytes.Span);

    public byte[] Serialize() =>
        FileTransferPayloadSerializer.SerializeCancel(this);
}
