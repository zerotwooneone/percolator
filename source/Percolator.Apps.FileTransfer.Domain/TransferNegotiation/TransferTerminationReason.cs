using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public sealed record TransferTerminationReason
{
    public string Value { get; }

    private TransferTerminationReason(string value) => Value = value;

    public static readonly TransferTerminationReason UserDeclined = new("UserDeclined");
    public static readonly TransferTerminationReason SenderCancelled = new("SenderCancelled");
    public static readonly TransferTerminationReason StorageExhausted = new("StorageExhausted");
    public static readonly TransferTerminationReason VerificationFailed = new("VerificationFailed");
    public static readonly TransferTerminationReason Timeout = new("Timeout");
    public static readonly TransferTerminationReason OfferExpired = new("OfferExpired");

    public static TransferTerminationReason FromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainValidationException("Termination reason cannot be null or whitespace.");
        return new TransferTerminationReason(text.Trim());
    }

    public override string ToString() => Value;
}
