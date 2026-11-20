using Percolator.Domain.Common;

namespace Percolator.Domain.Delivery.ValueObjects;

/// <summary>
/// Opaque relay-assigned identifier for a queued mailbox envelope. The relay owns the
/// format (currently a Guid "N" string); recipients treat it as an opaque acknowledgement
/// key — distinct from <see cref="QueueId"/>, which is the
/// <see cref="Percolator.Domain.Delivery.Hosting.RelayMailboxQueue"/> aggregate identity.
/// </summary>
public sealed record MailboxQueueId
{
    public const int MaxLength = 128;

    public string Value { get; }

    private MailboxQueueId(string value) => Value = value;

    public static DomainResult<MailboxQueueId> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DomainResult<MailboxQueueId>.Failure(new DomainError(
                "INVALID_MAILBOX_QUEUE_ID", "Mailbox queue id cannot be null, empty, or whitespace."));
        }

        if (value.Length > MaxLength)
        {
            return DomainResult<MailboxQueueId>.Failure(new DomainError(
                "INVALID_MAILBOX_QUEUE_ID",
                $"Mailbox queue id exceeds the maximum length of {MaxLength} characters."));
        }

        return DomainResult<MailboxQueueId>.Success(new MailboxQueueId(value));
    }

    public static MailboxQueueId Parse(string value)
    {
        var result = Create(value);
        if (result.IsFailure)
        {
            throw new ArgumentException(result.Error.Description, nameof(value));
        }

        return result.Value!;
    }

    public override string ToString() => Value;
}
