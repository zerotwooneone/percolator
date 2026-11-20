namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public enum FileTransferControlOpcode : byte
{
    Offer = 0x01,
    Accept = 0x02,
    Cancel = 0x03
}
